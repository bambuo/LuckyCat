using LuckyCat.Models;

namespace LuckyCat.Contracts;

public record ExchangeAccountDto(
    int Id,
    ExchangeType ExchangeType,
    string AccountName,
    string ApiKeyMasked,
    string ApiSecretMasked,
    string? PassphraseMasked,
    string? UserId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record ExchangeUpsertRequest(
    ExchangeType ExchangeType,
    string AccountName,
    string? ApiKey,
    string? ApiSecret,
    string? Passphrase,
    string? UserId);

public record ExchangeVerifyEvent(
    string Type,
    string? Check = null,
    string? Status = null,
    long? ElapsedMs = null,
    string? Detail = null,
    string? Evidence = null,
    int? Passed = null,
    int? Failed = null);

public record OutboundIpDto(string? Ip);
