using LuckyCat.Hosting;
using Xunit;

namespace LuckyCat.Tests;

public class HostOptionsTests
{
    [Fact]
    public void Development_keeps_cli_defaults()
    {
        var options = LuckyCatHostOptions.Development();

        Assert.Equal("http://localhost:5210", options.Urls);
        Assert.False(options.ServeStaticFiles);
        Assert.Equal(Path.Combine(options.DataDirectory, "luckycat.db"), options.DatabasePath);
    }

    [Fact]
    public void Desktop_binds_loopback_random_port_and_serves_static_files()
    {
        var options = LuckyCatHostOptions.Desktop("/tmp/luckycat-data");

        Assert.Equal("http://127.0.0.1:0", options.Urls);
        Assert.True(options.ServeStaticFiles);
        Assert.Equal(Path.Combine("/tmp/luckycat-data", "luckycat.db"), options.DatabasePath);
        Assert.Equal(Path.Combine(options.ContentRootPath, "wwwroot"), options.WebRootPath);
    }
}
