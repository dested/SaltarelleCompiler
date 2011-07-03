﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Saltarelle.Compiler.JSModel.Expressions {
    [Serializable]
    public class JsonExpression : Expression {
        public class ValueEntry {
            public string Name { get; private set; }
            public Expression Value { get; private set; }

            public ValueEntry(string name, Expression value) {
                if (name == null) throw new ArgumentNullException("name");
                if (!name.IsValidJavaScriptIdentifier()) throw new ArgumentException("name");
                if (value == null) throw new ArgumentNullException("value");

                Name  = name;
                Value = value;
            }
        }

        public override int Precedence { get { return ExpressionPrecedence.Terminal; } }

        public ReadOnlyCollection<ValueEntry> Values { get; private set; }

        public JsonExpression(IEnumerable<ValueEntry> values) {
            if (values == null) throw new ArgumentNullException("values");
            Values = values.AsReadOnly();
        }

        [System.Diagnostics.DebuggerStepThrough]
        public override TReturn Accept<TReturn, TData>(IExpressionVisitor<TReturn, TData> visitor, TData data) {
            return visitor.Visit(this, data);
        }
    }
}
