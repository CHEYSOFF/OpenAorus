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

    [Fact]
    public void Enable_refuses_a_dotnet_host_path()
    {
        // `dotnet run` makes Environment.ProcessPath the .NET host, not the app; registering a logon task
        // against it would "succeed" and never actually bring the tray icon back.
        Assert.False(StartupTask.Enable(@"C:\Program Files\dotnet\dotnet.exe"));
    }

    [Fact]
    public void IsPublishedExe_accepts_only_the_openaorus_exe_filename()
    {
        Assert.True(StartupTask.IsPublishedExe(@"C:\Tools\OpenAorus.exe"));
        Assert.True(StartupTask.IsPublishedExe(@"C:\Tools\openaorus.exe")); // case-insensitive
        Assert.False(StartupTask.IsPublishedExe(@"C:\Program Files\dotnet\dotnet.exe"));
    }
}
