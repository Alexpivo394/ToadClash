using Shouldly;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;

namespace Alexpivo394.Revit.Clash.Tests.Rules;

public sealed class ConditionTests
{
    private static ElementInfo Info(string? family = null, string? type = null) =>
        new("uid", 1, "OST_PipeCurves", family, type, "Level 1", "Workset1", []);

    private static PropertyRef Param(string name, Guid? guid = null) =>
        new(PropertyKind.Parameter, name, guid);

    [Test]
    public void Equals_CaseInsensitive()
    {
        var condition = new Condition(new PropertyRef(PropertyKind.Category), ConditionOperator.Equals, "ost_pipecurves");

        condition.Matches(Info(), []).ShouldBeTrue();
    }

    [Test]
    public void Contains_FamilyMufta()
    {
        var condition = new Condition(new PropertyRef(PropertyKind.Family), ConditionOperator.Contains, "муфта");

        condition.Matches(Info(family: "Муфта_ПП"), []).ShouldBeTrue();
    }

    [Test]
    public void NumericCondition_AcceptsCommaDecimal()
    {
        var condition = new Condition(Param("Diameter"), ConditionOperator.Greater, "40,5");
        var parameters = new[] { new ParameterValue("Diameter", null, "41", 41) };

        condition.Matches(Info(), parameters).ShouldBeTrue();
    }

    [Test]
    public void Greater_NonNumericValue_False()
    {
        var condition = new Condition(Param("Diameter"), ConditionOperator.Greater, "abc");
        var parameters = new[] { new ParameterValue("Diameter", null, "41", 41) };

        condition.Matches(Info(), parameters).ShouldBeFalse();
    }

    [Test]
    public void MissingParameter_NotEquals_True()
    {
        var condition = new Condition(Param("Absent"), ConditionOperator.NotEquals, "x");

        condition.Matches(Info(), []).ShouldBeTrue();
    }

    [Test]
    public void MissingParameter_Equals_False_IsEmpty_True()
    {
        new Condition(Param("Absent"), ConditionOperator.Equals, "x").Matches(Info(), []).ShouldBeFalse();
        new Condition(Param("Absent"), ConditionOperator.IsEmpty, null).Matches(Info(), []).ShouldBeTrue();
    }

    [Test]
    public void SharedGuid_TakesPrecedenceOverName()
    {
        var guid = Guid.NewGuid();
        var parameters = new[]
        {
            new ParameterValue("System", null, "wrong", null),
            new ParameterValue("Other name", guid, "right", null)
        };
        var condition = new Condition(Param("System", guid), ConditionOperator.Equals, "right");

        condition.Matches(Info(), parameters).ShouldBeTrue();
    }

    [Test]
    public void ParameterName_CaseInsensitive()
    {
        var parameters = new[] { new ParameterValue("System", null, "Heat", null) };

        new Condition(Param("system"), ConditionOperator.Equals, "heat").Matches(Info(), parameters).ShouldBeTrue();
    }

    [Test]
    public void Equals_UsesNumberWhenTextIsNull()
    {
        var parameters = new[] { new ParameterValue("Diameter", null, null, 12.5) };

        new Condition(Param("Diameter"), ConditionOperator.Equals, "12.5").Matches(Info(), parameters).ShouldBeTrue();
    }

    [Test]
    public void IsEmpty_BlankText_True_IsNotEmpty_False()
    {
        var parameters = new[] { new ParameterValue("Mark", null, "  ", null) };

        new Condition(Param("Mark"), ConditionOperator.IsEmpty, null).Matches(Info(), parameters).ShouldBeTrue();
        new Condition(Param("Mark"), ConditionOperator.IsNotEmpty, null).Matches(Info(), parameters).ShouldBeFalse();
    }

    [Test]
    public void Less_FallsBackToParsingText()
    {
        var parameters = new[] { new ParameterValue("Size", null, "10,5", null) };

        new Condition(Param("Size"), ConditionOperator.Less, "11").Matches(Info(), parameters).ShouldBeTrue();
    }

    [Test]
    public void NumericEquals_IgnoresDisplayFormat()
    {
        bool Equal(double number, string text, string expected) =>
            new Condition(Param("D"), ConditionOperator.Equals, expected).Matches(Info(), [new ParameterValue("D", null, text, number)]);

        Equal(100, "100 мм", "100").ShouldBeTrue();
        Equal(100, "100 мм", "100.0").ShouldBeTrue();
        Equal(99.9999999, "100 мм", "100").ShouldBeTrue();
        Equal(100.5, "101", "101").ShouldBeFalse();
        new Condition(Param("D"), ConditionOperator.NotEquals, "100,0").Matches(Info(), [new ParameterValue("D", null, "100 мм", 100)]).ShouldBeFalse();
    }

    [Test]
    public void NumericOneOf_ByNumber()
    {
        var parameters = new[] { new ParameterValue("D", null, "50 мм", 50) };

        new Condition(Param("D"), ConditionOperator.OneOf, null, ["40", "50,0"]).Matches(Info(), parameters).ShouldBeTrue();
        new Condition(Param("D"), ConditionOperator.OneOf, null, ["40", "60"]).Matches(Info(), parameters).ShouldBeFalse();
        // Нечисловое ожидаемое значение сравнивается как текст.
        new Condition(Param("D"), ConditionOperator.OneOf, null, ["50 мм"]).Matches(Info(), parameters).ShouldBeTrue();
    }

    [Test]
    public void SharedGuidNotFound_NoFallbackToName()
    {
        var parameters = new[] { new ParameterValue("System", null, "value", null) };
        var condition = new Condition(Param("System", Guid.NewGuid()), ConditionOperator.Equals, "value");

        condition.Matches(Info(), parameters).ShouldBeFalse();
    }
}
