namespace Auth.Server.Providers;

/// <summary>
/// What a callback read from the provider's identity token (spec 0011 → The provider identity and its address): the provider, the identity key
/// (Google <c>sub</c>, Microsoft <c>{tid}:{oid}</c>), the address as the provider gave it (<see langword="null"/>: no address), and whether the
/// address counts as verified. <c>ProviderIdentities</c> makes it.
/// </summary>
public sealed record ProviderIdentity(string Provider, string Key, string? Email, bool EmailVerified);
