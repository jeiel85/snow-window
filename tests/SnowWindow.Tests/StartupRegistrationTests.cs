using SnowWindow.App;

namespace SnowWindow.Tests;

public class StartupRegistrationTests
{
    private const string Current = "\"C:\\Users\\me\\Downloads\\SnowWindow-win-x64.exe\"";

    [Fact]
    public void Not_registered_is_left_alone()
    {
        Assert.False(StartupRegistration.NeedsPathUpdate(null, Current));
    }

    [Fact]
    public void Same_path_needs_no_update_regardless_of_case_or_padding()
    {
        Assert.False(StartupRegistration.NeedsPathUpdate(Current, Current));
        Assert.False(StartupRegistration.NeedsPathUpdate(Current.ToUpperInvariant(), Current));
        Assert.False(StartupRegistration.NeedsPathUpdate($" {Current} ", Current));
    }

    [Fact]
    public void Registration_pointing_at_another_copy_is_updated()
    {
        const string old = "\"D:\\Apps\\old\\SnowWindow.exe\"";
        Assert.True(StartupRegistration.NeedsPathUpdate(old, Current));
    }
}
