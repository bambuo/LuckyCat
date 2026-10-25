namespace LuckyCat.Contracts;

// 统一 API 包裹：成功 success/data，失败 success/error；error.message 必须可直接展示给用户。
public sealed record ApiError(string Code, string Message);
public sealed record ApiResponse<T>(bool Success, T? Data, ApiError? Error)
{
    public static ApiResponse<T> Ok(T data) => new(true, data, null);
    public static ApiResponse<T> Fail(string code, string message) => new(false, default, new ApiError(code, message));
}
