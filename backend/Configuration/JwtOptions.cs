namespace BackendServer.Configuration;

public class JwtOptions
{
    public const string Section = "JwtSettings";

    public string Key { get; set; } = "super_secret_key_that_is_long_enough_12345_and_even_longer_to_satisfy_hmacsha512_requirement";
    public string Issuer { get; set; } = "AutoSlideBackend";
    public string Audience { get; set; } = "AutoSlideUsers";
}
