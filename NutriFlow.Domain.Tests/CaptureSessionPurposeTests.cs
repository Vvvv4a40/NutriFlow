using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class CaptureSessionPurposeTests
{
    [Fact]
    public void Constructor_WithoutContext_PreservesDiaryDefaults()
    {
        CaptureSession session = new CaptureSession();

        Assert.Equal(MealSessionPurpose.Diary, session.Purpose);
        Assert.Empty(session.SavedDishNames);
        Assert.Equal(CaptureSessionState.Collecting, session.State);
        Assert.Empty(session.InputEvents);
    }

    [Theory]
    [InlineData(MealSessionPurpose.Diary)]
    [InlineData(MealSessionPurpose.CreateDish)]
    public void Constructor_WithPurpose_StoresPurpose(MealSessionPurpose purpose)
    {
        CaptureSession session = new CaptureSession(purpose);

        Assert.Equal(purpose, session.Purpose);
    }

    [Fact]
    public void Constructor_WithUndefinedPurpose_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CaptureSession((MealSessionPurpose)int.MaxValue));
    }

    [Fact]
    public void Constructor_WithSavedDishNames_CopiesAndTrimsNames()
    {
        List<string> names = [" Рагу ", "Омлет"];
        CaptureSession session = new CaptureSession(savedDishNames: names);

        names[0] = "Изменено";
        names.Clear();

        Assert.Equal(new[] { "Рагу", "Омлет" }, session.SavedDishNames);
    }

    [Fact]
    public void SavedDishNames_CannotBeChangedThroughExposedCollection()
    {
        CaptureSession session = new CaptureSession(savedDishNames: ["Рагу"]);
        ICollection<string> names = Assert.IsAssignableFrom<ICollection<string>>(
            session.SavedDishNames);

        Assert.True(names.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => names.Clear());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithBlankSavedDishName_ThrowsArgumentException(string? name)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new CaptureSession(savedDishNames: [name!]));
    }

    [Fact]
    public void Constructor_WithLongSavedDishName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new CaptureSession(savedDishNames: [new string('x', 201)]));
    }

    [Fact]
    public void Constructor_WithMaximumLengthSavedDishName_PreservesName()
    {
        string name = new string('x', 200);
        CaptureSession session = new CaptureSession(savedDishNames: [name]);

        Assert.Equal(name, Assert.Single(session.SavedDishNames));
    }
}
