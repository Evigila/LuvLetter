namespace LuvLetter.Core.Tests;

internal static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new SmokeTestException(message ?? "Expected true, but found false.");
        }
    }

    public static void False(bool condition, string? message = null) =>
        True(!condition, message ?? "Expected false, but found true.");

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new SmokeTestException(
                message ?? $"Expected <{expected}>, but found <{actual}>.");
        }
    }

    public static void NotEqual<T>(T notExpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
        {
            throw new SmokeTestException(
                message ?? $"Did not expect <{actual}>.");
        }
    }

    public static T NotNull<T>(T? value, string? message = null)
        where T : class
    {
        if (value is null)
        {
            throw new SmokeTestException(message ?? "Expected a non-null value.");
        }

        return value;
    }

    public static void SequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string? message = null)
    {
        var expectedArray = expected.ToArray();
        var actualArray = actual.ToArray();
        if (!expectedArray.SequenceEqual(actualArray))
        {
            throw new SmokeTestException(
                message
                ?? $"Expected [{string.Join(", ", expectedArray)}], "
                + $"but found [{string.Join(", ", actualArray)}].");
        }
    }

    public static void Empty<T>(IEnumerable<T> values, string? message = null)
    {
        if (values.Any())
        {
            throw new SmokeTestException(message ?? "Expected an empty sequence.");
        }
    }

    public static void AllFinite(params float[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (!float.IsFinite(values[index]))
            {
                throw new SmokeTestException(
                    $"Expected finite value at index {index}, but found {values[index]}.");
            }
        }
    }

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new SmokeTestException(
            $"Expected {typeof(TException).Name}, but no exception was thrown.");
    }
}

internal sealed class SmokeTestException(string message) : Exception(message);
