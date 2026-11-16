using System;

namespace TESL.Accelerator.Tests
{
    public static class Assert
    {
        public static void True(bool condition, string message = "")
        {
            if (!condition)
            {
                throw new Exception($"Assertion Failed [Expected True, Got False]: {message}");
            }
        }

        public static void False(bool condition, string message = "")
        {
            if (condition)
            {
                throw new Exception($"Assertion Failed [Expected False, Got True]: {message}");
            }
        }

        public static void Equal<T>(T expected, T actual, string message = "")
        {
            if (!Equals(expected, actual))
            {
                throw new Exception($"Assertion Failed [Expected '{expected}', Got '{actual}']: {message}");
            }
        }

        public static void Contains(string expectedSubstring, string actualString, string message = "")
        {
            if (actualString == null || !actualString.Contains(expectedSubstring))
            {
                throw new Exception($"Assertion Failed [String does not contain '{expectedSubstring}']: {message}");
            }
        }

        public static void DoesNotContain(string forbiddenSubstring, string actualString, string message = "")
        {
            if (actualString != null && actualString.Contains(forbiddenSubstring))
            {
                throw new Exception($"Assertion Failed [String contains forbidden '{forbiddenSubstring}']: {message}");
            }
        }
    }
}
