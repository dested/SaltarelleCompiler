﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ICSharpCode.NRefactory.TypeSystem;
using Saltarelle.Compiler.JSModel;
using Saltarelle.Compiler.JSModel.TypeSystem;
using Saltarelle.Compiler.ScriptSemantics;

namespace Saltarelle.Compiler.MetadataImporter {
	// Done:
	// [ScriptName] (Type | Method | Property | Field | Event)
	// [IgnoreNamespace] (Type)
	// [ScriptNamespaceAttribute] (Type)
	// [PreserveName] (Type | Method | Property | Field | Event)
	// [PreserveCase] (Method | Property | Field | Event)
	// [ScriptSkip] (Method)
	// [AlternateSignature] (Method)
	// [ScriptAlias] (Method | Property)
	// [InlineCode] (Method)
	// [InstanceMethodOnFirstArgument] (Method)
	// [IgnoreGenericArguments] (Method)
	// [NonScriptable] (Type | Method | Property | Field | Event)
	// [IntrinsicProperty] (Property (/indexer))
	// [GlobalMethods] (Class)
	// [Imported] (Type)

	// To handle:
	// [NonScriptable] (Constructor)
	// [ScriptAssembly] (Assembly) ?
	// [ScriptQualifier] (Assembly)
	// [ScriptNamespaceAttribute] (Assembly)
	// [Resources] (Class) ?
	// [Mixin] (Class) ?
	// [NamedValues] (Enum) - Needs better support in the compiler
	// [NumericValues] (Enum)
	// [AlternateSignature] (Constructor)
	// Record
	// Anonymous types

	public class ScriptSharpMetadataImporter : INamingConventionResolver {
		private const string ScriptSkipAttribute = "ScriptSkipAttribute";
		private const string ScriptAliasAttribute = "ScriptAliasAttribute";
		private const string InlineCodeAttribute = "InlineCodeAttribute";
		private const string InstanceMethodOnFirstArgumentAttribute = "InstanceMethodOnFirstArgumentAttribute";
		private const string NonScriptableAttribute = "NonScriptableAttribute";
		private const string IgnoreGenericArgumentsAttribute = "IgnoreGenericArgumentsAttribute";
		private const string IgnoreNamespaceAttribute = "IgnoreNamespaceAttribute";
		private const string ScriptNamespaceAttribute = "ScriptNamespaceAttribute";
		private const string AlternateSignatureAttribute = "AlternateSignatureAttribute";
		private const string ScriptNameAttribute = "ScriptNameAttribute";
		private const string PreserveNameAttribute = "PreserveNameAttribute";
		private const string PreserveCaseAttribute = "PreserveCaseAttribute";
		private const string IntrinsicPropertyAttribute = "IntrinsicPropertyAttribute";
		private const string GlobalMethodsAttribute = "GlobalMethodsAttribute";
		private const string ImportedAttribute = "ImportedAttribute";

		/// <summary>
		/// Used to deterministically order members. It is assumed that all members belong to the same type.
		/// </summary>
		private class MemberOrderer : IComparer<IMember> {
			public static readonly MemberOrderer Instance = new MemberOrderer();

			private MemberOrderer() {
			}

			private int CompareMethods(IMethod x, IMethod y) {
				int result = string.CompareOrdinal(x.Name, y.Name);
				if (result != 0)
					return result;
				if (x.Parameters.Count > y.Parameters.Count)
					return 1;
				else if (x.Parameters.Count < y.Parameters.Count)
					return -1;

				var xparms = string.Join(",", x.Parameters.Select(p => p.Type.FullName));
				var yparms = string.Join(",", y.Parameters.Select(p => p.Type.FullName));

				return string.CompareOrdinal(xparms, yparms);
			}

			public int Compare(IMember x, IMember y) {
				if (x is IMethod) {
					if (y is IMethod) {
						return CompareMethods((IMethod)x, (IMethod)y);
					}
					else
						return -1;
				}
				else if (y is IMethod) {
					return 1;
				}

				if (x is IProperty) {
					if (y is IProperty) {
						return string.CompareOrdinal(x.Name, y.Name);
					}
					else 
						return -1;
				}
				else if (y is IProperty) {
					return 1;
				}

				if (x is IField) {
					if (y is IField) {
						return string.CompareOrdinal(x.Name, y.Name);
					}
					else 
						return -1;
				}
				else if (y is IField) {
					return 1;
				}

				if (x is IEvent) {
					if (y is IEvent) {
						return string.CompareOrdinal(x.Name, y.Name);
					}
					else 
						return -1;
				}
				else if (y is IEvent) {
					return 1;
				}

				throw new ArgumentException("Invalid member type" + x.GetType().FullName);
			}
		}

		private class TypeSemantics {
			public TypeScriptSemantics Semantics { get; private set; }
			public bool GlobalMethods { get; private set; }

			public TypeSemantics(TypeScriptSemantics semantics, bool globalMethods) {
				Semantics     = semantics;
				GlobalMethods = globalMethods;
			}
		}

		private Dictionary<ITypeDefinition, TypeSemantics> _typeSemantics;
		private Dictionary<ITypeDefinition, Dictionary<string, List<IMember>>> _memberNamesByType;
		private Dictionary<IMethod, MethodScriptSemantics> _methodSemantics;
		private Dictionary<IProperty, PropertyScriptSemantics> _propertySemantics;
		private Dictionary<IField, FieldScriptSemantics> _fieldSemantics;
		private Dictionary<IEvent, EventScriptSemantics> _eventSemantics;
		private Dictionary<string, string> _errors;
		private Dictionary<IAssembly, int> _internalInterfaceMemberCountPerAssembly;
		private bool _minimizeNames;

		public ScriptSharpMetadataImporter(bool minimizeNames) {
			_minimizeNames = minimizeNames;
		}

		private string MakeCamelCase(string s) {
			if (string.IsNullOrEmpty(s))
				return s;
			if (s.Equals("ID", StringComparison.Ordinal))
				return "id";

			bool hasNonUppercase = false;
			int numUppercaseChars = 0;
			for (int index = 0; index < s.Length; index++) {
				if (char.IsUpper(s, index)) {
					numUppercaseChars++;
				}
				else {
					hasNonUppercase = true;
					break;
				}
			}

			if ((!hasNonUppercase && s.Length != 1) || numUppercaseChars == 0)
				return s;
			else if (numUppercaseChars > 1)
				return s.Substring(0, numUppercaseChars - 1).ToLower(CultureInfo.InvariantCulture) + s.Substring(numUppercaseChars - 1);
			else if (s.Length == 1)
				return s.ToLower(CultureInfo.InvariantCulture);
			else
				return char.ToLower(s[0], CultureInfo.InvariantCulture) + s.Substring(1);
		}

		private static readonly string encodeNumberTable = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
		private string EncodeNumber(int i) {
			string result = encodeNumberTable.Substring(i % encodeNumberTable.Length, 1);
			while (i >= encodeNumberTable.Length) {
				i /= encodeNumberTable.Length;
				result = encodeNumberTable.Substring(i % encodeNumberTable.Length, 1) + result;
			}
			return result;
		}

		private string GetDefaultTypeName(ITypeDefinition def) {
			int outerCount = (def.DeclaringTypeDefinition != null ? def.DeclaringTypeDefinition.TypeParameters.Count : 0);
			return def.Name + (def.TypeParameterCount != outerCount ? "$" + (def.TypeParameterCount - outerCount).ToString(CultureInfo.InvariantCulture) : "");
		}

		private IList<object> GetAttributePositionalArgs(IEntity entity, string attributeName) {
			attributeName = "System.Runtime.CompilerServices." + attributeName;
			var attr = entity.Attributes.FirstOrDefault(a => a.AttributeType.FullName == attributeName);
			return attr != null ? attr.PositionalArguments.Select(arg => arg.ConstantValue).ToList() : null;
		}

		private string DetermineNamespace(ITypeDefinition typeDefinition) {
			while (typeDefinition.DeclaringTypeDefinition != null) {
				typeDefinition = typeDefinition.DeclaringTypeDefinition;
			}

			var ina = GetAttributePositionalArgs(typeDefinition, IgnoreNamespaceAttribute);
			var sna = GetAttributePositionalArgs(typeDefinition, ScriptNamespaceAttribute);
			if (ina != null) {
				if (sna != null) {
					_errors[typeDefinition.FullName + ":Namespace"] = "The type " + typeDefinition.FullName + " has both [IgnoreNamespace] and [ScriptNamespace] specified. At most one of these attributes can be specified for a type.";
					return typeDefinition.FullName;
				}
				else {
					return "";
				}
			}
			else {
				if (sna != null) {
					string arg = (string)sna[0];
					if (arg == null || (arg != "" && !arg.IsValidNestedJavaScriptIdentifier()))
						_errors[typeDefinition.FullName + ":Namespace"] = typeDefinition.FullName + ": The argument for [ScriptNamespace], when applied to a type, must be a valid JavaScript qualified identifier.";
					return arg;
				}
				else {
					return typeDefinition.Namespace;
				}
			}
		}

		private Tuple<string, string> SplitName(string typeName) {
			int dot = typeName.LastIndexOf('.');
			return dot > 0 ? Tuple.Create(typeName.Substring(0, dot), typeName.Substring(dot + 1)) : Tuple.Create("", typeName);
		}

		private void DetermineTypeSemantics(ITypeDefinition typeDefinition) {
			if (_typeSemantics.ContainsKey(typeDefinition))
				return;

			if (GetAttributePositionalArgs(typeDefinition, NonScriptableAttribute) != null || typeDefinition.DeclaringTypeDefinition != null && GetTypeSemantics(typeDefinition.DeclaringTypeDefinition).Type == TypeScriptSemantics.ImplType.NotUsableFromScript) {
				_typeSemantics[typeDefinition] = new TypeSemantics(TypeScriptSemantics.NotUsableFromScript(), false);
				return;
			}

			var scriptNameAttr = GetAttributePositionalArgs(typeDefinition, ScriptNameAttribute);
			bool isImported = GetAttributePositionalArgs(typeDefinition, ImportedAttribute) != null;
			bool preserveName = isImported || GetAttributePositionalArgs(typeDefinition, PreserveNameAttribute) != null;

			string typeName, nmspace;
			if (scriptNameAttr != null && scriptNameAttr[0] != null && ((string)scriptNameAttr[0]).IsValidJavaScriptIdentifier()) {
				typeName = (string)scriptNameAttr[0];
				nmspace = DetermineNamespace(typeDefinition);
			}
			else {
				if (scriptNameAttr != null) {
					_errors[typeDefinition.FullName + ":Name"] = typeDefinition.FullName + ": The argument for [ScriptName], when applied to a type, must be a valid JavaScript identifier.";
				}

				if (_minimizeNames && !Utils.IsPublic(typeDefinition) && !preserveName) {
					nmspace = DetermineNamespace(typeDefinition);
					int index = _typeSemantics.Values.Where(ts => ts.Semantics.Type == TypeScriptSemantics.ImplType.NormalType).Select(ts => SplitName(ts.Semantics.Name)).Count(tn => tn.Item1 == nmspace && tn.Item2.StartsWith("$"));
					typeName = "$" + index.ToString(CultureInfo.InvariantCulture);
				}
				else {
					typeName = GetDefaultTypeName(typeDefinition);
					if (typeDefinition.DeclaringTypeDefinition != null) {
						if (GetAttributePositionalArgs(typeDefinition, IgnoreNamespaceAttribute) != null || GetAttributePositionalArgs(typeDefinition, ScriptNamespaceAttribute) != null) {
							_errors[typeDefinition.FullName + ":Namespace"] = "[IgnoreNamespace] or [ScriptNamespace] cannot be specified for the nested type " + typeDefinition.FullName + ".";
						}

						typeName = GetTypeSemantics(typeDefinition.DeclaringTypeDefinition).Name + "$" + typeName;
						nmspace = "";
					}
					else {
						nmspace = DetermineNamespace(typeDefinition);
					}

					if (!Utils.IsPublic(typeDefinition) && !preserveName && !typeName.StartsWith("$")) {
						typeName = "$" + typeName;
					}
				}
			}

			bool globalMethods = false;
			var globalMethodsAttr = GetAttributePositionalArgs(typeDefinition, GlobalMethodsAttribute);
			if (globalMethodsAttr != null) {
				if (!typeDefinition.IsStatic) {
					_errors[typeDefinition.FullName + ":GlobalMethods"] = "The type " + typeDefinition.FullName + " must be static in order to be decorated with a [GlobalMethodsAttribute]";
				}
				else if (typeDefinition.Fields.Any() || typeDefinition.Events.Any() || typeDefinition.Properties.Any()) {
					_errors[typeDefinition.FullName + ":GlobalMethods"] = "The type " + typeDefinition.FullName + " cannot have any fields, events or properties in order to be decorated with a [GlobalMethodsAttribute]";
				}
				else if (typeDefinition.DeclaringTypeDefinition != null) {
					_errors[typeDefinition.FullName + ":GlobalMethods"] = "[GlobalMethodsAttribute] cannot be applied to the nested type " + typeDefinition.FullName + ".";
				}
				else {
					nmspace = "";
					globalMethods = true;
				}
			}

			_typeSemantics[typeDefinition] = new TypeSemantics(TypeScriptSemantics.NormalType(!string.IsNullOrEmpty(nmspace) ? nmspace + "." + typeName : typeName, generateCode: !isImported), globalMethods);
		}

		private Dictionary<string, List<IMember>> GetMemberNames(ITypeDefinition typeDefinition) {
			DetermineTypeSemantics(typeDefinition);
			Dictionary<string, List<IMember>> result;
			if (!_memberNamesByType.TryGetValue(typeDefinition, out result))
				_memberNamesByType[typeDefinition] = result = ProcessType(typeDefinition);
			return result;
		}

		private Dictionary<string, List<IMember>> GetMemberNames(IEnumerable<ITypeDefinition> typeDefinitions) {
			return (from def in typeDefinitions from m in GetMemberNames(def) group m by m.Key into g select new { g.Key, Value = g.SelectMany(x => x.Value).ToList() }).ToDictionary(x => x.Key, x => x.Value);
		}

		private Tuple<string, bool> DeterminePreferredMemberName(IMember member) {
			var asa = GetAttributePositionalArgs(member, AlternateSignatureAttribute);
			if (asa != null) {
				var otherMembers = member.DeclaringTypeDefinition.Methods.Where(m => m.Name == member.Name && GetAttributePositionalArgs(m, AlternateSignatureAttribute) == null).ToList();
				if (otherMembers.Count == 1) {
					return DeterminePreferredMemberName(otherMembers[0]);
				}
				else {
					_errors[GetQualifiedMemberName(member) + ":NoMainMethod"] = "The member " + GetQualifiedMemberName(member) + " has an [AlternateSignatureAttribute], but there is not exactly one other method with the same name that does not have that attribute.";
					return Tuple.Create(member.Name, false);
				}
			}

			var sna = GetAttributePositionalArgs(member, ScriptNameAttribute);
			if (sna != null) {
				string name = (string)sna[0];
				if (name != "" && !name.IsValidJavaScriptIdentifier()) {
					_errors[GetQualifiedMemberName(member) + ":InvalidName"] = "The name specified in the [ScriptName] attribute for type method " + GetQualifiedMemberName(member) + " must be a valid JavaScript identifier, or be blank.";
				}
				return Tuple.Create(name, true);
			}
			var pca = GetAttributePositionalArgs(member, PreserveCaseAttribute);
			if (pca != null)
				return Tuple.Create(member.Name, true);
			bool preserveName =    GetAttributePositionalArgs(member, PreserveNameAttribute) != null
			                    || GetAttributePositionalArgs(member, InstanceMethodOnFirstArgumentAttribute) != null
			                    || GetAttributePositionalArgs(member, IntrinsicPropertyAttribute) != null
			                    || _typeSemantics[member.DeclaringTypeDefinition].GlobalMethods;
			if (preserveName)
				return Tuple.Create(MakeCamelCase(member.Name), true);

			if (Utils.IsPublic(member)) {
				return Tuple.Create(MakeCamelCase(member.Name), false);
			}
			else {
				if (_minimizeNames)
					return Tuple.Create((string)null, false);
				else
					return Tuple.Create("$" + MakeCamelCase(member.Name), false);
			}
		}

		public string GetQualifiedMemberName(IMember member) {
			return member.DeclaringType.FullName + "." + member.Name;
		}

		private Dictionary<string, List<IMember>> ProcessType(ITypeDefinition typeDefinition) {
			var allMembers = GetMemberNames(typeDefinition.GetAllBaseTypeDefinitions().Where(x => x != typeDefinition));
			foreach (var m in allMembers.Where(kvp => kvp.Value.Count > 1)) {
				// TODO: Determine if we need to raise an error here.
			}

			var membersByName =   from m in typeDefinition.GetMembers(options: GetMemberOptions.IgnoreInheritedMembers)
			                       let name = DeterminePreferredMemberName(m)
			                     group new { m, name } by name.Item1 into g
			                    select new { Name = g.Key, Members = g.Select(x => new { Member = x.m, NameSpecified = x.name.Item2 }).ToList() };

			foreach (var current in membersByName) {
				foreach (var m in current.Members.OrderByDescending(x => x.NameSpecified).ThenBy(x => x.Member, MemberOrderer.Instance)) {
					if (m.Member is IMethod) {
						var method = (IMethod)m.Member;

						if (method.IsConstructor) {
							// TODO
						}
						else {
							DetermineMethodSemantics(method, current.Name, m.NameSpecified, allMembers);
						}
					}
					else if (m.Member is IProperty) {
						DeterminePropertySemantics((IProperty)m.Member, current.Name, m.NameSpecified, allMembers);
					}
					else if (m.Member is IField) {
						DetermineFieldSemantics((IField)m.Member, current.Name, m.NameSpecified, allMembers);
					}
					else if (m.Member is IEvent) {
						DetermineEventSemantics((IEvent)m.Member, current.Name, m.NameSpecified, allMembers);
					}
				}
			}

			return allMembers;
		}

		private static void AddMember(Dictionary<string, List<IMember>> allMembers, string name, IMember member) {
			if (allMembers.ContainsKey(name))
				allMembers[name].Add(member);
			else
				allMembers[name] = new List<IMember> { member };
		}

		private string GetUniqueName(IMember member, string preferredName, Dictionary<string, List<IMember>> allMembers) {
			// The name was not explicitly specified, so ensure that we have a unique name.
			if (preferredName == null && member.DeclaringTypeDefinition.Kind == TypeKind.Interface) {
				// Minimized interface names need to be unique within the assembly, otherwise we have a very high risk of collisions (100% when a type implements more than one internal interface).
				int c;
				_internalInterfaceMemberCountPerAssembly.TryGetValue(member.ParentAssembly, out c);
				_internalInterfaceMemberCountPerAssembly[member.ParentAssembly] = ++c;
				return "$I" + EncodeNumber(c);
			}
			else {
				string name = preferredName;
				int i = (name == null ? 0 : 1);
				while (name == null || allMembers.ContainsKey(name)) {
					name = preferredName + "$" + EncodeNumber(i);
					i++;
				}
				return name;
			}
		}

		private void DeterminePropertySemantics(IProperty property, string preferredName, bool nameSpecified, Dictionary<string, List<IMember>> allMembers) {
			if (_typeSemantics[property.DeclaringTypeDefinition].Semantics.Type == TypeScriptSemantics.ImplType.NotUsableFromScript || GetAttributePositionalArgs(property, NonScriptableAttribute) != null) {
				_propertySemantics[property] = PropertyScriptSemantics.NotUsableFromScript();
				return;
			}

			var saa = GetAttributePositionalArgs(property, ScriptAliasAttribute);

			if (saa != null) {
				if (property.IsIndexer) {
					_errors[GetQualifiedMemberName(property) + ":IndexerCannotHaveScriptAlias"] = "The indexer on type " + property.DeclaringType.FullName + " cannot have a [ScriptAliasAttribute].";
				}
				else if (!property.IsStatic) {
					_errors[GetQualifiedMemberName(property) + ":InstancePropertyCannotHaveScriptAlias"] = "The property " + GetQualifiedMemberName(property) + " cannot have a [ScriptAliasAttribute] because it is an instance member.";
				}
				else {
					string alias = (string)saa[0] ?? "";
					_propertySemantics[property] = PropertyScriptSemantics.GetAndSetMethods(property.CanGet ? MethodScriptSemantics.InlineCode(alias) : null, property.CanSet ? MethodScriptSemantics.InlineCode(alias) : null);
					return;
				}
			}

			var ipa = GetAttributePositionalArgs(property, IntrinsicPropertyAttribute);
			if (ipa != null) {
				if (property.DeclaringType.Kind == TypeKind.Interface) {
					if (property.IsIndexer)
						_errors[GetQualifiedMemberName(property) + ":InterfacePropertyCannotBeIntrinsic"] = "The indexer on type " + property.DeclaringType.FullName + " cannot have an [IntrinsicPropertyAttribute] because it is an interface member.";
					else
						_errors[GetQualifiedMemberName(property) + ":InterfacePropertyCannotBeIntrinsic"] = "The property " + GetQualifiedMemberName(property) + " cannot have an [IntrinsicPropertyAttribute] because it is an interface member.";
				}
				else if (property.IsOverride) {
					if (property.IsIndexer)
						_errors[GetQualifiedMemberName(property) + ":OverridingPropertyCannotBeIntrinsic"] = "The indexer on type " + property.DeclaringType.FullName + " cannot have an [IntrinsicPropertyAttribute] because it overrides a base member.";
					else
						_errors[GetQualifiedMemberName(property) + ":OverridingPropertyCannotBeIntrinsic"] = "The property " + GetQualifiedMemberName(property) + " cannot have an [IntrinsicPropertyAttribute] because it overrides a base member.";
				}
				else if (property.IsOverridable) {
					if (property.IsIndexer)
						_errors[GetQualifiedMemberName(property) + ":OverridablePropertyCannotBeIntrinsic"] = "The indexer on type " + property.DeclaringType.FullName + " cannot have an [IntrinsicPropertyAttribute] because it is overridable.";
					else
						_errors[GetQualifiedMemberName(property) + ":OverridablePropertyCannotBeIntrinsic"] = "The property " + GetQualifiedMemberName(property) + " cannot have an [IntrinsicPropertyAttribute] because it is overridable.";
				}
				else if (property.ImplementedInterfaceMembers.Count > 0) {
					if (property.IsIndexer)
						_errors[GetQualifiedMemberName(property) + ":ImplementingPropertyCannotBeIntrinsic"] = "The indexer on type" + property.DeclaringType.FullName + " cannot have an [IntrinsicPropertyAttribute] because it implements an interface member.";
					else
						_errors[GetQualifiedMemberName(property) + ":ImplementingPropertyCannotBeIntrinsic"] = "The property " + GetQualifiedMemberName(property) + " cannot have an [IntrinsicPropertyAttribute] because it implements an interface member.";
				}
				else if (property.IsIndexer) {
					if (property.Parameters.Count == 1) {
						_propertySemantics[property] = PropertyScriptSemantics.GetAndSetMethods(property.CanGet ? MethodScriptSemantics.NativeIndexer() : null, property.CanSet ? MethodScriptSemantics.NativeIndexer() : null);
						return;
					}
					else {
						_errors[GetQualifiedMemberName(property) + ":NativeIndexerArgument"] = "The indexer for type " + property.DeclaringType.FullName + " must have exactly one parameter in order to have an [IntrinsicPropertyAttribute].";
					}
				}
				else {
					AddMember(allMembers, preferredName, property);
					_propertySemantics[property] = PropertyScriptSemantics.Field(preferredName);
					return;
				}
			}

			MethodScriptSemantics getter, setter;
			if (property.CanGet) {
				var getterName = DeterminePreferredMemberName(property.Getter);
				if (!getterName.Item2)
					getterName = Tuple.Create(!nameSpecified && _minimizeNames && !Utils.IsPublic(property) ? null : (nameSpecified ? "get_" + preferredName : GetUniqueName(property, "get_" + preferredName, allMembers)), false);	// If the name was not specified, generate one.

				DetermineMethodSemantics(property.Getter, getterName.Item1, getterName.Item2, allMembers);
				getter = _methodSemantics[property.Getter];
			}
			else {
				getter = null;
			}

			if (property.CanSet) {
				var setterName = DeterminePreferredMemberName(property.Setter);
				if (!setterName.Item2)
					setterName = Tuple.Create(!nameSpecified && _minimizeNames && !Utils.IsPublic(property) ? null : (nameSpecified ? "set_" + preferredName : GetUniqueName(property, "set_" + preferredName, allMembers)), false);	// If the name was not specified, generate one.

				DetermineMethodSemantics(property.Setter, setterName.Item1, setterName.Item2, allMembers);
				setter = _methodSemantics[property.Setter];
			}
			else {
				setter = null;
			}

			_propertySemantics[property] = PropertyScriptSemantics.GetAndSetMethods(getter, setter);
		}

		private void DetermineMethodSemantics(IMethod method, string preferredName, bool nameSpecified, Dictionary<string, List<IMember>> allMembers) {
			var ssa = GetAttributePositionalArgs(method, ScriptSkipAttribute);
			var saa = GetAttributePositionalArgs(method, ScriptAliasAttribute);
			var ica = GetAttributePositionalArgs(method, InlineCodeAttribute);
			var ifa = GetAttributePositionalArgs(method, InstanceMethodOnFirstArgumentAttribute);
			var nsa = GetAttributePositionalArgs(method, NonScriptableAttribute);
			var iga = GetAttributePositionalArgs(method, IgnoreGenericArgumentsAttribute);

			if (nsa != null) {
				_methodSemantics[method] = MethodScriptSemantics.NotUsableFromScript();
				return;
			}
			else if (_typeSemantics[method.DeclaringTypeDefinition].Semantics.Type == TypeScriptSemantics.ImplType.NotUsableFromScript) {
				_methodSemantics[method] = MethodScriptSemantics.NotUsableFromScript();
				return;
			}
			else if (_typeSemantics[method.DeclaringTypeDefinition].GlobalMethods) {
				_methodSemantics[method] = MethodScriptSemantics.NormalMethod(preferredName, isGlobal: true);
				return;
			}
			else if (ssa != null) {
				// [ScriptSkip] - Skip invocation of the method entirely.
				if (method.DeclaringTypeDefinition.Kind == TypeKind.Interface) {
					_errors[GetQualifiedMemberName(method) + ":ScriptSkipOnInterfaceMember"] = "The member " + GetQualifiedMemberName(method) + " cannot have a [ScriptSkipAttribute] because it is an interface method.";
					_methodSemantics[method] = MethodScriptSemantics.NormalMethod(method.Name);
					return;
				}
				else if (method.IsOverride) {
					_errors[GetQualifiedMemberName(method) + ":ScriptSkipOnOverridable"] = "The member " + GetQualifiedMemberName(method) + " cannot have a [ScriptSkipAttribute] because it overrides a base member.";
					_methodSemantics[method] = MethodScriptSemantics.NormalMethod(method.Name);
					return;
				}
				else if (method.IsOverridable) {
					_errors[GetQualifiedMemberName(method) + ":ScriptSkipOnOverridable"] = "The member " + GetQualifiedMemberName(method) + " cannot have a [ScriptSkipAttribute] because it is overridable.";
					_methodSemantics[method] = MethodScriptSemantics.NormalMethod(method.Name);
					return;
				}
				else if (method.ImplementedInterfaceMembers.Count > 0) {
					_errors[GetQualifiedMemberName(method) + ":ScriptSkipOnInterfaceImplementation"] = "The member " + GetQualifiedMemberName(method) + " cannot have a [ScriptSkipAttribute] because it implements an interface member.";
					_methodSemantics[method] = MethodScriptSemantics.NormalMethod(method.Name);
					return;
				}
				else {
					if (method.IsStatic) {
						if (method.Parameters.Count != 1)
							_errors[GetQualifiedMemberName(method) + ":ScriptSkipParameterCount"] = "The static method " + GetQualifiedMemberName(method) + " must have exactly one parameter in order to have a [ScriptSkipAttribute].";
						_methodSemantics[method] = MethodScriptSemantics.InlineCode("{0}");
						return;
					}
					else {
						if (method.Parameters.Count != 0)
							_errors[GetQualifiedMemberName(method) + ":ScriptSkipParameterCount"] = "The instance method " + GetQualifiedMemberName(method) + " must have no parameters in order to have a [ScriptSkipAttribute].";
						_methodSemantics[method] = MethodScriptSemantics.InlineCode("{this}");
						return;
					}
				}
			}
			else if (saa != null) {
				if (method.IsStatic) {
					_methodSemantics[method] = MethodScriptSemantics.InlineCode((string) saa[0] + "(" + string.Join(", ", method.Parameters.Select(p => "{" + p.Name + "}")) + ")");
					return;
				}
				else {
					_errors[GetQualifiedMemberName(method) + ":NonStaticWithAlias"] = "The method " + GetQualifiedMemberName(method) + " must be static in order to have a [ScriptAliasAttribute].";
					_methodSemantics[method] = MethodScriptSemantics.NormalMethod(method.Name);
					return;
				}
			}
			else if (ica != null) {
				if (method.DeclaringTypeDefinition.Kind == TypeKind.Interface) {
					_errors[GetQualifiedMemberName(method) + ":InlineCodeOnInterfaceMember"] = "The member " + GetQualifiedMemberName(method) + " cannot have an [InlineCodeAttribute] because it is an interface method.";
					_methodSemantics[method] = MethodScriptSemantics.NormalMethod(method.Name);
					return;
				}
				else if (method.IsOverride) {
					_errors[GetQualifiedMemberName(method) + ":InlineCodeOnOverridable"] = "The member " + GetQualifiedMemberName(method) + " cannot have an [InlineCodeAttribute] because it overrides a base member.";
					_methodSemantics[method] = MethodScriptSemantics.NormalMethod(method.Name);
					return;
				}
				else if (method.IsOverridable) {
					_errors[GetQualifiedMemberName(method) + ":InlineCodeOnOverridable"] = "The member " + GetQualifiedMemberName(method) + " cannot have an [InlineCodeAttribute] because it is overridable.";
					_methodSemantics[method] = MethodScriptSemantics.NormalMethod(method.Name);
					return;
				}
				else if (method.ImplementedInterfaceMembers.Count > 0) {
					_errors[GetQualifiedMemberName(method) + ":ScriptSkipOnInterfaceImplementation"] = "The member " + GetQualifiedMemberName(method) + " cannot have a [InlineCodeAttribute] because it implements an interface member.";
					_methodSemantics[method] = MethodScriptSemantics.NormalMethod(method.Name);
					return;
				}
				else {
					string code = (string) ica[0];

					foreach (var ph in new Regex("\\{([a-zA-Z_][a-zA-Z0-9]*)\\}").Matches(code).Cast<Match>().Select(x => x.Groups[1].Value)) {
						if (ph == "this") {
							if (method.IsStatic) {
								_errors[GetQualifiedMemberName(method) + ":InlineCodeInvalidPlaceholder"] = "Cannot use the placeholder {this} in inline code for the static method " + GetQualifiedMemberName(method) + ".";
								code = "X";
							}
						}
						else if (!method.Parameters.Any(p => p.Name == ph) && !method.TypeParameters.Any(p => p.Name == ph) && !method.DeclaringType.GetDefinition().TypeParameters.Any(p => p.Name == ph)) {
							_errors[GetQualifiedMemberName(method) + ":InlineCodeInvalidPlaceholder"] = "Invalid placeholder {" + ph + "} in inline code for method " + GetQualifiedMemberName(method) + ".";
							code = "X";
						}
					}

					_methodSemantics[method] = MethodScriptSemantics.InlineCode(code);
					return;
				}
			}
			else if (ifa != null) {
				if (method.IsStatic) {
					_methodSemantics[method] = MethodScriptSemantics.InstanceMethodOnFirstArgument(preferredName);
					return;
				}
				else {
					_errors[GetQualifiedMemberName(method) + ":InstanceMethodOnFirstArgument"] = "The method " + GetQualifiedMemberName(method) + " cannot have an [InstanceMethodOnFirstArgumentAttribute] because it is not static.";
					_methodSemantics[method] = MethodScriptSemantics.NormalMethod(method.Name);
					return;
				}
			}
			else {
				if (method.IsOverride) {
					if (nameSpecified) {
						_errors[GetQualifiedMemberName(method) + ":CannotSpecifyName"] = "The [ScriptName], [PreserveName] and [PreserveCase] attributes cannot be specified on method the method " + GetQualifiedMemberName(method) + " because it overrides a base member. Specify the attribute on the base member instead.";
					}
					if (iga != null) {
						_errors[GetQualifiedMemberName(method) + ":CannotSpecifyIgnoreGenericArguments"] = "The [IgnoreGenericArgumentsAttribute] attribute cannot be specified on the method " + GetQualifiedMemberName(method) + " because it overrides a base member. Specify the attribute on the base member instead.";
					}

					var semantics = _methodSemantics[(IMethod)InheritanceHelper.GetBaseMember(method)];
					if (semantics.Type == MethodScriptSemantics.ImplType.NormalMethod) {
						var errorMethod = method.ImplementedInterfaceMembers.FirstOrDefault(im => GetMethodSemantics((IMethod)im.MemberDefinition).Name != semantics.Name);
						if (errorMethod != null) {
							_errors[GetQualifiedMemberName(method) + ":MultipleInterfaceImplementations"] = "The overriding member " + GetQualifiedMemberName(method) + " cannot implement the interface method " + GetQualifiedMemberName(errorMethod) + " because it has a different script name. Consider using explicit interface implementation";
						}
					}

					_methodSemantics[method] = semantics;
					return;
				}
				else if (method.ImplementedInterfaceMembers.Count > 0) {
					if (nameSpecified) {
						_errors[GetQualifiedMemberName(method) + ":CannotSpecifyName"] = "The [ScriptName], [PreserveName] and [PreserveCase] attributes cannot be specified on the method " + GetQualifiedMemberName(method) + " because it implements an interface member. Specify the attribute on the interface member instead, or consider using explicit interface implementation.";
					}

					if (method.ImplementedInterfaceMembers.Select(im => GetMethodSemantics((IMethod)im.MemberDefinition).Name).Distinct().Count() > 1) {
						_errors[GetQualifiedMemberName(method) + ":MultipleInterfaceImplementations"] = "The member " + GetQualifiedMemberName(method) + " cannot implement multiple interface methods with differing script names. Consider using explicit interface implementation.";
					}

					_methodSemantics[method] = _methodSemantics[(IMethod) method.ImplementedInterfaceMembers[0].MemberDefinition];
					return;
				}
				else {
					if (preferredName == "") {
						// Special case - Script# supports setting the name of a method to an empty string, which means that it simply removes the name (eg. "x.M(a)" becomes "x(a)"). We model this with literal code.
						if (method.DeclaringTypeDefinition.Kind == TypeKind.Interface) {
							_errors[GetQualifiedMemberName(method) + ":InterfaceMethodWithEmptyName"] = "The member " + GetQualifiedMemberName(method) + " cannot have an empty name specified in its [ScriptName] because it is an interface method.";
							_methodSemantics[method] = MethodScriptSemantics.NormalMethod(method.Name);
							return;
						}
						else if (method.IsOverridable) {
							_errors[GetQualifiedMemberName(method) + ":OverridableWithEmptyName"] = "The member " + GetQualifiedMemberName(method) + " cannot have an empty name specified in its [ScriptName] because it is overridable.";
							_methodSemantics[method] = MethodScriptSemantics.NormalMethod(method.Name);
							return;
						}
						else if (method.IsStatic) {
							_errors[GetQualifiedMemberName(method) + ":StaticWithEmptyName"] = "The member " + GetQualifiedMemberName(method) + " cannot have an empty name specified in its [ScriptName] because it is static.";
							_methodSemantics[method] = MethodScriptSemantics.NormalMethod(method.Name);
							return;
						}
						else {
							_methodSemantics[method] = MethodScriptSemantics.InlineCode("{this}(" + string.Join(", ", method.Parameters.Select(p => "{" + p.Name + "}")) + ")");
							return;
						}
					}
					else {
						string name = nameSpecified ? preferredName : GetUniqueName(method, preferredName, allMembers);
						AddMember(allMembers, name, method);
						_methodSemantics[method] = MethodScriptSemantics.NormalMethod(name, generateCode: GetAttributePositionalArgs(method, AlternateSignatureAttribute) == null, ignoreGenericArguments: iga != null);
						return;
					}
				}
			}
		}

		private void DetermineEventSemantics(IEvent evt, string preferredName, bool nameSpecified, Dictionary<string, List<IMember>> allMembers) {
			if (_typeSemantics[evt.DeclaringTypeDefinition].Semantics.Type == TypeScriptSemantics.ImplType.NotUsableFromScript || GetAttributePositionalArgs(evt, NonScriptableAttribute) != null) {
				_eventSemantics[evt] = EventScriptSemantics.NotUsableFromScript();
				return;
			}

			MethodScriptSemantics adder, remover;
			if (evt.CanAdd) {
				var getterName = DeterminePreferredMemberName(evt.AddAccessor);
				if (!getterName.Item2)
					getterName = Tuple.Create(!nameSpecified && _minimizeNames && !Utils.IsPublic(evt) ? null : (nameSpecified ? "add_" + preferredName : GetUniqueName(evt, "add_" + preferredName, allMembers)), false);	// If the name was not specified, generate one.

				DetermineMethodSemantics(evt.AddAccessor, getterName.Item1, getterName.Item2, allMembers);
				adder = _methodSemantics[evt.AddAccessor];
			}
			else {
				adder = null;
			}

			if (evt.CanRemove) {
				var setterName = DeterminePreferredMemberName(evt.RemoveAccessor);
				if (!setterName.Item2)
					setterName = Tuple.Create(!nameSpecified && _minimizeNames && !Utils.IsPublic(evt) ? null : (nameSpecified ? "remove_" + preferredName : GetUniqueName(evt, "remove_" + preferredName, allMembers)), false);	// If the name was not specified, generate one.

				DetermineMethodSemantics(evt.RemoveAccessor, setterName.Item1, setterName.Item2, allMembers);
				remover = _methodSemantics[evt.RemoveAccessor];
			}
			else {
				remover = null;
			}

			_eventSemantics[evt] = EventScriptSemantics.AddAndRemoveMethods(adder, remover);
		}

		private void DetermineFieldSemantics(IField field, string preferredName, bool nameSpecified, Dictionary<string, List<IMember>> allMembers) {
			if (_typeSemantics[field.DeclaringTypeDefinition].Semantics.Type == TypeScriptSemantics.ImplType.NotUsableFromScript || GetAttributePositionalArgs(field, NonScriptableAttribute) != null) {
				_fieldSemantics[field] = FieldScriptSemantics.NotUsableFromScript();
			}
			else {
				string name = nameSpecified ? preferredName : GetUniqueName(field, preferredName, allMembers);
				AddMember(allMembers, name, field);
				_fieldSemantics[field] = FieldScriptSemantics.Field(name);
			}
		}

		public void Prepare(IEnumerable<ITypeDefinition> types, IAssembly mainAssembly, IErrorReporter errorReporter) {
			_internalInterfaceMemberCountPerAssembly = new Dictionary<IAssembly, int>();
			_errors = new Dictionary<string, string>();
			var l = types.ToList();
			_typeSemantics = new Dictionary<ITypeDefinition, TypeSemantics>();
			_memberNamesByType = new Dictionary<ITypeDefinition, Dictionary<string, List<IMember>>>();
			_methodSemantics = new Dictionary<IMethod, MethodScriptSemantics>();
			_propertySemantics = new Dictionary<IProperty, PropertyScriptSemantics>();
			_fieldSemantics = new Dictionary<IField, FieldScriptSemantics>();
			_eventSemantics = new Dictionary<IEvent, EventScriptSemantics>();
			foreach (var t in l.Where(t => t.ParentAssembly == mainAssembly || Utils.IsPublic(t))) {
				DetermineTypeSemantics(t);
				GetMemberNames(t);
			}

			foreach (var e in _errors.Values)
				errorReporter.Error(e);
		}

		public TypeScriptSemantics GetTypeSemantics(ITypeDefinition typeDefinition) {
			return _typeSemantics[typeDefinition].Semantics;
		}

		public string GetTypeParameterName(ITypeParameter typeParameter) {
			throw new NotImplementedException();
		}

		public MethodScriptSemantics GetMethodSemantics(IMethod method) {
			return _methodSemantics[method];
		}

		public ConstructorScriptSemantics GetConstructorSemantics(IMethod method) {
			throw new NotImplementedException();
		}

		public PropertyScriptSemantics GetPropertySemantics(IProperty property) {
			return _propertySemantics[property];
		}

		public string GetAutoPropertyBackingFieldName(IProperty property) {
			throw new NotImplementedException();
		}

		public FieldScriptSemantics GetFieldSemantics(IField field) {
			return _fieldSemantics[field];
		}

		public EventScriptSemantics GetEventSemantics(IEvent evt) {
			return _eventSemantics[evt];
		}

		public string GetAutoEventBackingFieldName(IEvent evt) {
			throw new NotImplementedException();
		}

		public string GetEnumValueName(IField value) {
			throw new NotImplementedException();
		}

		public string GetVariableName(IVariable variable, ISet<string> usedNames) {
			throw new NotImplementedException();
		}

		public string GetTemporaryVariableName(int index) {
			throw new NotImplementedException();
		}

		public string ThisAlias {
			get { throw new NotImplementedException(); }
		}
	}
}
