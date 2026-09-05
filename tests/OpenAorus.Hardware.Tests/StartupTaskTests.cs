using OpenAorus.Hardware.Platform;

namespace OpenAorus.Hardware.Tests;

public class StartupTaskTests
{
    [Fact]
    public void Create_arguments_register_logon_task_with_highest_privileges()
    {
        var args = StartupTask.BuildCreateArguments(@"C:\Tools\OpenAorus.exe");
        Assert.Contains("/Create", args);
        Assert.Contains("/SC ONLOGON", args);
        Assert.Contains("/RL HIGHEST", args);
        Assert.Contains("/TN \"OpenAorus\"", args);
        Assert.Contains("/TR \"\\\"C:\\Tools\\OpenAorus.exe\\\" --tray\"", args);
        Assert.Contains("/F", args);
    }

    [Fact]
    public void Delete_and_query_arguments_target_the_task()
    {
        Assert.Equal("/Delete /TN \"OpenAorus\" /F", StartupTask.BuildDeleteArguments());
        Assert.Equal("/Query /TN \"OpenAorus\"", StartupTask.BuildQueryArguments());
    }
}
