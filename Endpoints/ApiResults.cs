using LuckyCat.Contracts;

namespace LuckyCat.Endpoints;

/// <summary>
/// 端点统一响应工厂（LC-STD-03 §1.2/§1.4/§6）：成功 success/data，创建 201+完整资源，
/// 失败 success/error{code,message}+约定状态码；message 必须可直接展示给用户。
/// </summary>
internal static class ApiResults
{
    public static IResult Ok<T>(T data) =>
        Results.Json(ApiResponse<T>.Ok(data), ApiJson.Options, statusCode: StatusCodes.Status200OK);

    public static IResult Created<T>(T data) =>
        Results.Json(ApiResponse<T>.Ok(data), ApiJson.Options, statusCode: StatusCodes.Status201Created);

    public static IResult Fail(string code, string message, int statusCode) =>
        Results.Json(ApiResponse<object>.Fail(code, message), ApiJson.Options, statusCode: statusCode);
}
