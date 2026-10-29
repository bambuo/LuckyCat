namespace LuckyCat.Hosting;

/// <summary>应用管道的运行期参数：CLI 模式与桌面模式共用同一份构建入口。</summary>
public sealed record LuckyCatHostOptions(
    string ContentRootPath,
    string DataDirectory,
    string Urls,
    bool ServeStaticFiles)
{
    public string DatabasePath => Path.Combine(DataDirectory, "luckycat.db");

    public string WebRootPath => Path.Combine(ContentRootPath, "wwwroot");

    public static LuckyCatHostOptions Development() => new(
        Directory.GetCurrentDirectory(),
        Directory.GetCurrentDirectory(),
        "http://localhost:5210",
        ServeStaticFiles: false);

    public static LuckyCatHostOptions Desktop(string dataDirectory) => new(
        AppContext.BaseDirectory,
        dataDirectory,
        "http://127.0.0.1:0",
        ServeStaticFiles: true);
}
