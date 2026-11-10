using System;
using System.Collections.Generic;
using System.Globalization;

// The subset of the NUnit 3 attribute and Assert API that the test files use, so they run without NuGet.
namespace NUnit.Framework
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class TestFixtureAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class TestCaseAttribute : Attribute
    {
        public TestCaseAttribute(params object[] arguments)
        {
            Arguments = arguments ?? new object[] { null };
        }

        public object[] Arguments { get; }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class SetUpAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TearDownAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class OneTimeSetUpAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class OneTimeTearDownAttribute : Attribute
    {
    }

    public class AssertionException : Exception
    {
        public AssertionException(string message)
            : base(message)
        {
        }
    }

    public delegate void TestDelegate();

    public static class Assert
    {
        public static void Fail(string message = "")
        {
            throw new AssertionException(message);
        }

        public static void IsTrue(bool condition, string message = "")
        {
            if (!condition)
                throw new AssertionException("expected true. " + message);
        }

        public static void IsFalse(bool condition, string message = "")
        {
            if (condition)
                throw new AssertionException("expected false. " + message);
        }

        public static void IsNull(object value, string message = "")
        {
            if (value != null)
                throw new AssertionException("expected null but was <" + value + ">. " + message);
        }

        public static void IsNotNull(object value, string message = "")
        {
            if (value == null)
                throw new AssertionException("expected a value but was null. " + message);
        }

        public static void AreEqual(object expected, object actual, string message = "")
        {
            if (!ValuesEqual(expected, actual))
                throw new AssertionException("expected <" + Show(expected) + "> but was <" + Show(actual) + ">. " + message);
        }

        public static void AreEqual(double expected, double actual, double delta, string message = "")
        {
            if (Math.Abs(expected - actual) > delta)
                throw new AssertionException("expected <" + expected.ToString(CultureInfo.InvariantCulture) + "> +/- " + delta.ToString(CultureInfo.InvariantCulture) +
                                             " but was <" + actual.ToString(CultureInfo.InvariantCulture) + ">. " + message);
        }

        public static void AreNotEqual(object expected, object actual, string message = "")
        {
            if (ValuesEqual(expected, actual))
                throw new AssertionException("expected a value other than <" + Show(expected) + ">. " + message);
        }

        public static void AreSame(object expected, object actual, string message = "")
        {
            if (!ReferenceEquals(expected, actual))
                throw new AssertionException("expected the same instance. " + message);
        }

        public static void AreNotSame(object expected, object actual, string message = "")
        {
            if (ReferenceEquals(expected, actual))
                throw new AssertionException("expected different instances. " + message);
        }

        public static void Greater(IComparable actual, IComparable limit, string message = "")
        {
            if (Compare(actual, limit) <= 0)
                throw new AssertionException("expected <" + Show(actual) + "> to be greater than <" + Show(limit) + ">. " + message);
        }

        public static void GreaterOrEqual(IComparable actual, IComparable limit, string message = "")
        {
            if (Compare(actual, limit) < 0)
                throw new AssertionException("expected <" + Show(actual) + "> to be at least <" + Show(limit) + ">. " + message);
        }

        public static void Less(IComparable actual, IComparable limit, string message = "")
        {
            if (Compare(actual, limit) >= 0)
                throw new AssertionException("expected <" + Show(actual) + "> to be less than <" + Show(limit) + ">. " + message);
        }

        public static void LessOrEqual(IComparable actual, IComparable limit, string message = "")
        {
            if (Compare(actual, limit) > 0)
                throw new AssertionException("expected <" + Show(actual) + "> to be at most <" + Show(limit) + ">. " + message);
        }

        public static T Throws<T>(TestDelegate code, string message = "") where T : Exception
        {
            try
            {
                code();
            }
            catch (T expected)
            {
                return expected;
            }
            catch (Exception other)
            {
                throw new AssertionException("expected " + typeof(T).Name + " but " + other.GetType().Name + " was thrown: " + other.Message + ". " + message);
            }
            throw new AssertionException("expected " + typeof(T).Name + " but nothing was thrown. " + message);
        }

        private static int Compare(IComparable actual, IComparable limit)
        {
            if (IsNumeric(actual) && IsNumeric(limit))
                return Convert.ToDouble(actual, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(limit, CultureInfo.InvariantCulture));
            return actual.CompareTo(limit);
        }

        private static bool IsNumeric(object value)
        {
            return value is sbyte || value is byte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong ||
                   value is float || value is double || value is decimal;
        }

        private static bool ValuesEqual(object expected, object actual)
        {
            if (expected == null || actual == null)
                return expected == null && actual == null;
            if (IsNumeric(expected) && IsNumeric(actual))
                return Convert.ToDouble(expected, CultureInfo.InvariantCulture) == Convert.ToDouble(actual, CultureInfo.InvariantCulture);
            if (expected is System.Collections.IEnumerable left && actual is System.Collections.IEnumerable right && !(expected is string))
            {
                System.Collections.IEnumerator a = left.GetEnumerator();
                System.Collections.IEnumerator b = right.GetEnumerator();
                while (true)
                {
                    bool hasA = a.MoveNext();
                    bool hasB = b.MoveNext();
                    if (hasA != hasB)
                        return false;
                    if (!hasA)
                        return true;
                    if (!ValuesEqual(a.Current, b.Current))
                        return false;
                }
            }
            return expected.Equals(actual);
        }

        private static string Show(object value)
        {
            return value == null ? "null" : value.ToString();
        }
    }

    public static class StringAssert
    {
        public static void Contains(string expected, string actual, string message = "")
        {
            if (actual == null || actual.IndexOf(expected, StringComparison.Ordinal) < 0)
                throw new AssertionException("expected <" + actual + "> to contain <" + expected + ">. " + message);
        }

        public static void DoesNotContain(string expected, string actual, string message = "")
        {
            if (actual != null && actual.IndexOf(expected, StringComparison.Ordinal) >= 0)
                throw new AssertionException("expected <" + actual + "> not to contain <" + expected + ">. " + message);
        }

        public static void StartsWith(string expected, string actual, string message = "")
        {
            if (actual == null || !actual.StartsWith(expected, StringComparison.Ordinal))
                throw new AssertionException("expected <" + actual + "> to start with <" + expected + ">. " + message);
        }
    }
}
