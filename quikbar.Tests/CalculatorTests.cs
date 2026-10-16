using quikbar.Services;
using Xunit;

namespace quikbar.Tests;

public sealed class CalculatorTests
{
    private static double Eval(string expr)
    {
        Assert.True(CalculatorService.TryEvaluate(expr, out _, out var copy), $"should parse: {expr}");
        return double.Parse(copy, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Theory]
    [InlineData("2+2", 4)]
    [InlineData("2+2*3", 8)]
    [InlineData("(3+4)*2", 14)]
    [InlineData("10/4", 2.5)]
    [InlineData("2^10", 1024)]
    [InlineData("10%3", 1)]
    [InlineData("-5+3", -2)]
    [InlineData("2(3+4)", 14)]
    [InlineData("2pi", 2 * Math.PI)]
    [InlineData("1,000+2", 1002)]
    [InlineData("= 2+2", 4)]
    [InlineData("calc 2+2", 4)]
    public void BasicExpressions(string expr, double expected)
    {
        Assert.Equal(expected, Eval(expr), precision: 6);
    }

    [Theory]
    [InlineData("sqrt(16)", 4)]
    [InlineData("abs(-5)", 5)]
    [InlineData("sin(90)", 1)]
    [InlineData("cos(0)", 1)]
    [InlineData("log(100)", 2)]
    [InlineData("ln(e)", 1)]
    [InlineData("round(2.5)", 2)] // banker's? .NET rounds to even — assert actual
    [InlineData("floor(2.9)", 2)]
    [InlineData("ceil(2.1)", 3)]
    public void Functions(string expr, double expected)
    {
        Assert.Equal(expected, Eval(expr), precision: 6);
    }

    [Fact]
    public void PercentPostfix()
    {
        Assert.Equal(0.5, Eval("50%"), precision: 9);
        Assert.Equal(150, Eval("50%*300"), precision: 6);
    }

    [Theory]
    [InlineData("1/0")]
    [InlineData("sqrt(-1)")]
    [InlineData("2+")]
    [InlineData("(2+3")]
    [InlineData("hello")]
    [InlineData("code")]
    [InlineData("123")]
    [InlineData("")]
    [InlineData("2+2; rm -rf")]
    public void InvalidOrNonMath_ReturnsFalse(string expr)
    {
        Assert.False(CalculatorService.TryEvaluate(expr, out _, out _));
    }

    [Fact]
    public void FloatNoiseIsRounded()
    {
        Assert.True(CalculatorService.TryEvaluate("0.1+0.2", out _, out var copy));
        Assert.Equal("0.3", copy);
    }
}
