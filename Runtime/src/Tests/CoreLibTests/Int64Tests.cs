﻿using System;
using System.Collections.Generic;
using System.Testing;
using System.Text;

namespace CoreLibTests {
	[TestFixture]
	public class Int64Tests {
		[Test]
		public void TypePropertiesAreCorrect() {
			Assert.IsTrue((object)(long)0 is long);
			Assert.IsFalse((object)0.5 is long);
			Assert.AreEqual(typeof(long).FullName, "ss.Int32");
			Assert.IsFalse(typeof(long).IsClass);
		}

		private T GetDefaultValue<T>() {
			return default(T);
		}

		[Test]
		public void DefaultValueIs0() {
			Assert.AreStrictEqual(GetDefaultValue<long>(), 0);
		}

		[Test]
		public void DefaultConstructorReturnsZero() {
			Assert.AreStrictEqual(new long(), 0);
		}

		[Test]
		public void FormatWorks() {
			Assert.AreEqual(((long)0x123).Format("x"), "123");
		}

		[Test]
		public void LocaleFormatWorks() {
			Assert.AreEqual(((long)0x123).LocaleFormat("x"), "123");
		}

		[Test]
		public void ParseWithoutRadixWorks() {
			Assert.AreEqual(long.Parse("234"), 234);
		}

		[Test]
		public void ParseWithRadixWorks() {
			Assert.AreEqual(long.Parse("234", 16), 0x234);
		}

		[Test]
		public void ToStringWithoutRadixWorks() {
			Assert.AreEqual(((long)123).ToString(), "123");
		}

		[Test]
		public void ToStringWithRadixWorks() {
			Assert.AreEqual(((long)123).ToString(10), "123");
			Assert.AreEqual(((long)0x123).ToString(16), "123");
		}
	}
}
