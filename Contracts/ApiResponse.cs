using System.Text.Json.Serialization;

namespace LuckyCat.Contracts;

// 统一 API 包裹：成功 success/data，失败 success/error；error.message 必须可直接展示给用户（LC-STD-03 §1.2）。
// 包裹层的 data/error 互斥：成功不带 error、失败不带 data（示例 §1.2 逐字对齐）；
// data 内部的 null 是业务语义（未知/未提供），不受此处影响。
public sealed record ApiError(string Code, string Message);

public sealed record ApiResponse<T>(
    bool Success,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] T? Data,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ApiError? Error)
{
    public static ApiResponse<T> Ok(T data) => new(true, data, null);
    public static ApiResponse<T> Fail(string code, string message) => new(false, default, new ApiError(code, message));
}
