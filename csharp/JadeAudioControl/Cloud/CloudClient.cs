using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using JadeAudioControl.Compat;
using JadeAudioControl.Protocol;

namespace JadeAudioControl.Cloud;

public class CloudException : Exception
{
    public CloudException(string message) : base(message) { }
}

public sealed class AuthException : CloudException
{
    public AuthException(string message) : base(message) { }
}

/// <summary>One preset from the online library.</summary>
public sealed class CloudPreset
{
    public string Name { get; init; } = "Untitled";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public int DeviceType { get; init; } = -1;
    public double GlobalGain { get; init; }
    public string ShareCode { get; init; } = "";
    public int Downloads { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public List<Band> Bands { get; init; } = new();

    public string Summary => Tags.Count > 0
        ? $"{Bands.Count} bands · {string.Join(", ", Tags)}"
        : $"{Bands.Count} bands";

    public static CloudPreset FromJson(Json node)
    {
        var bandsNode = node["eqParamsJson"].Exists ? node["eqParamsJson"] : node["peqList"];
        var bands = new List<Band>();
        int i = 0;
        foreach (var item in bandsNode.Items)
        {
            bands.Add(new Band
            {
                Index = item["position"].AsInt(i),
                Frequency = item["frequency"].AsInt(1000),
                Gain = item["gain"].AsDouble(0),
                Q = item["qValue"].AsDouble(1),
                Type = (FilterType)item["filterType"].AsInt(0),
            });
            i++;
        }

        var tags = new List<string>();
        foreach (var tag in node["tag"].Items)
        {
            string? text = tag.AsString;
            if (!string.IsNullOrEmpty(text))
                tags.Add(text!);
        }

        return new CloudPreset
        {
            Name = node["styleName"].AsString ?? node["name"].AsString ?? "Untitled",
            Author = node["peqUserName"].AsString ?? node["userName"].AsString ?? "",
            Description = node["description"].AsString ?? "",
            DeviceType = node["deviceType"].AsInt(-1),
            GlobalGain = node["masterGain"].AsDouble(0),
            ShareCode = node["shareCode"].AsString ?? "",
            Downloads = node["downloadSum"].AsInt(0),
            Tags = tags,
            Bands = bands,
        };
    }
}

public sealed class PresetPage
{
    public PresetPage(IReadOnlyList<CloudPreset> presets, int total)
    {
        Presets = presets;
        Total = total;
    }

    public IReadOnlyList<CloudPreset> Presets { get; }
    public int Total { get; }
}

/// <summary>
/// Talks to FiiO's preset library and account system.
///
/// Preset requests are wrapped in a hybrid envelope: a random AES-256 key
/// encrypts the JSON body, and that key travels RSA-encrypted next to it. The
/// preset service carries no Authorization header at all - the caller is named
/// by a userId inside the encrypted body - so browsing works with no account.
/// </summary>
public sealed class CloudClient
{
    public const string BaseUrl = "https://fiiocontrol.fiio.com";
    private const string Ucenter = "/ucenter-api";
    private const string UserSystem = "/usersystem-api";
    public const string RegisterUrl = "https://usersystem.fiio.com/sso/register";
    public const string PrivacyUrl = "https://www.fiio.com/privacypolicy";
    public const string AgreementUrl = "https://www.fiio.com/yhxy";

    private const string ClientId = "ELP6WFJ6Q0VXV6J3";
    private const string ClientSecret = "63ZEP47VJEFCBPMXQJD3X1ZHLMM44AAK";

    /// <summary>Wraps the AES key for the preset service.</summary>
    private const string PresetKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA0mPGrgZPDCPDd9m129qy
        +TV5HeLWDTJbNK5wlUAemqH3NyrOh3HUo+LbTbMf6J45AFDjNRCDiZZc3Y+uCici
        9fHn0dixbtuHOdJer0U/4xHloOgYmsTwhAh56njWQAyaqoi0R3nG0bpCgsi5omlS
        RqP5KyaybuKjPyZvGVn0IKnVVQx3AI1+p/5lWARv23nPNS9ehfKts5oeFdOAKgT2
        mvV80TzJHbc2mKU8XoBr0VgX4Ohgq/A+Ddv0Wz0bNAQJgzrFAN1lIg2NqktcGdzD
        /HHPIeGCYG2gB7ZADsGX6vGpSoeUj3anr25nojQVbZgEBeE/6nJZ793Qhq8vudep
        XQIDAQAB
        -----END PUBLIC KEY-----
        """;

    /// <summary>A different key, used only by the user_info call.</summary>
    private const string UserKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA6rHbVXvKG4oRara64ulw
        uJYBVMJx2nilsn/ktOhA08tIKe9NPMIf+BYZrZ6g4mRyZhTtFMza2n5kDJfEHp6g
        AOyuuKtwsEbYSyRQNTySpHPi2YDNmD8mrw060Qcifoy2aupqZncsDMNk9HRkWhqI
        eYk2V3tk7eBoZWqDR13w4nueEne8gThw+IdGMn5eV6Sd7v/4wBiYplTo7rG1VznC
        IzncS2Dv8JEvmpgzFEgvScSe3BXdCMIJ9j8n/GeLb/J3R3I8zUqpA9xZDVwnfw1l
        L62sYv2RhZtnLyndmZ4uJuQrdrU8dZKXGOo370uht8p8AxWAlZfvGAlTkQGzqmFD
        OQIDAQAB
        -----END PUBLIC KEY-----
        """;

    public static readonly IReadOnlyDictionary<string, int> DeviceTypes = new Dictionary<string, int>
    {
        ["JadeAudio JA11"] = 109,
        ["FIIO KA17"] = 108,
        ["FIIO KA15"] = 110,
        ["FIIO FP3"] = 111,
        ["FIIO FX17"] = 112,
        ["FIIO QX13"] = 113,
        ["SNOWSKY Melody"] = 114,
        ["SNOWSKY TINY A"] = 115,
        ["SNOWSKY TINY B"] = 117,
        ["JadeAudio JIEZI"] = 118,
        ["FIIO LS-TC2"] = 119,
        ["FIIO FG3"] = 120,
        ["FIIO QX11"] = 121,
        ["OAK NANO"] = 122,
        ["FIIO BTR13"] = 31,
        ["FIIO BTR15"] = 24,
        ["FIIO BTR17"] = 35,
        ["RETRO NANO"] = 38,
        ["FIIO K19"] = 29,
        ["FIIO BT11"] = 30,
    };

    public static int DeviceTypeFor(string productName) =>
        DeviceTypes.TryGetValue(productName, out var id) ? id : -1;

    private readonly HttpClient _http;

    // Account state. Tokens live here in memory only - nothing reaches disk.
    private string? _appToken;
    public string? AccessToken { get; private set; }
    public string? UserId { get; private set; }
    public string UserName { get; private set; } = "";
    public bool LoggedIn => AccessToken is not null && UserId is not null;

    public CloudClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.Referrer = new Uri(BaseUrl + "/");
        _http.DefaultRequestHeaders.Add("Origin", BaseUrl);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("JadeAudioControl/2.0");
    }

    // -- envelope ------------------------------------------------------------

    /// <summary>A 32-character hex string, used verbatim as 32 AES key bytes.</summary>
    private static byte[] NewKey()
    {
        var raw = new byte[16];
        using (var rng = new RNGCryptoServiceProvider())
            rng.GetBytes(raw);
        return Encoding.ASCII.GetBytes(BitConverter.ToString(raw).Replace("-", string.Empty).ToLowerInvariant());
    }

    private static string AesEncrypt(byte[] key, string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var data = Encoding.UTF8.GetBytes(plaintext);
        return Convert.ToBase64String(encryptor.TransformFinalBlock(data, 0, data.Length));
    }

    private static string AesDecrypt(byte[] key, string cipherBase64)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        var data = Convert.FromBase64String(cipherBase64);
        return Encoding.UTF8.GetString(decryptor.TransformFinalBlock(data, 0, data.Length));
    }

    private static string RsaWrap(string pem, byte[] key)
    {
        using var rsa = Pem.LoadPublicKey(pem);
        return Convert.ToBase64String(rsa.Encrypt(key, RSAEncryptionPadding.Pkcs1));
    }

    private async Task<Json> PostEnvelopeAsync(string path, object payload, string? accept = null)
    {
        var key = NewKey();
        var body = new Dictionary<string, object>
        {
            ["cipherSign"] = RsaWrap(PresetKeyPem, key),
            ["cipherText"] = AesEncrypt(key, Json.Write(payload)),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + Ucenter + path)
        {
            Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Request-ID", Guid.NewGuid().ToString());
        if (accept is not null)
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(accept));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            throw new CloudException($"Could not reach the preset server: {exc.Message}");
        }

        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!Json.TryParse(text, out var parsed))
            throw new CloudException($"Unexpected reply from {path}.");

        if (parsed["code"].AsInt() != 200)
        {
            string message = parsed["msg"].AsString ?? parsed["detail"].AsString ?? $"{path} failed";
            throw new CloudException(message);
        }

        var data = parsed["data"];
        // A successful payload arrives as one AES blob under "data".
        return data.IsString ? Json.Parse(AesDecrypt(key, data.AsString!)) : data;
    }

    private static IEnumerable<Json> Records(Json data)
    {
        if (data.IsArray)
            return data.Items;
        return data["records"].IsArray ? data["records"].Items : Array.Empty<Json>();
    }

    // -- browsing ------------------------------------------------------------

    /// <summary>Presets shared by other owners - the web app's "Handpick" list.</summary>
    public async Task<PresetPage> CommunityAsync(int deviceType, int page = 1, int pageSize = 20)
    {
        var payload = new Dictionary<string, object>
        {
            ["pageNumber"] = page,
            ["pageSize"] = pageSize,
            ["totalRow"] = -1,
        };
        if (deviceType >= 0)
            payload["deviceType"] = new[] { deviceType };

        var data = await PostEnvelopeAsync("/get-share-peq", payload, "application/vnd.fiio.v1+json")
            .ConfigureAwait(false);
        return new PresetPage(
            Records(data).Select(CloudPreset.FromJson).ToList(),
            data["totalRow"].AsInt());
    }

    /// <summary>FiiO's own presets for a device.</summary>
    public async Task<PresetPage> OfficialAsync(int deviceType, int page = 1, int pageSize = 20)
    {
        var payload = new Dictionary<string, object>
        {
            ["pageNumber"] = page,
            ["pageSize"] = pageSize,
            ["totalRows"] = -1,
        };
        if (deviceType >= 0)
            payload["deviceType"] = deviceType;

        var data = await PostEnvelopeAsync("/get-peq-official-list", payload).ConfigureAwait(false);
        return new PresetPage(
            Records(data).Select(CloudPreset.FromJson).ToList(),
            data["totalRows"].AsInt());
    }

    public async Task<PresetPage> SearchAsync(string keyword, int deviceType)
    {
        var payload = new Dictionary<string, object> { ["searchValue"] = keyword };
        if (deviceType >= 0)
            payload["deviceType"] = deviceType;

        var data = await PostEnvelopeAsync("/search-peq", payload, "application/vnd.fiio.v1+json")
            .ConfigureAwait(false);
        var list = Records(data).Select(CloudPreset.FromJson).ToList();
        return new PresetPage(list, list.Count);
    }

    public async Task<CloudPreset> ByShareCodeAsync(string shareCode)
    {
        var code = shareCode.Trim();
        if (code.Length == 0)
            throw new CloudException("Enter a share code.");

        var data = await PostEnvelopeAsync(
            "/get-share-peq", new Dictionary<string, object> { ["shareCode"] = code },
            "application/vnd.fiio.v1+json").ConfigureAwait(false);

        var first = Records(data).FirstOrDefault();
        if (first is null)
            throw new CloudException("No preset found for that share code.");
        return CloudPreset.FromJson(first);
    }

    /// <summary>The signed-in account's own presets.</summary>
    public async Task<PresetPage> PersonalAsync(int deviceType)
    {
        if (!LoggedIn)
            throw new AuthException("Sign in to see the presets saved on your account.");

        var payload = new Dictionary<string, object> { ["userId"] = UserId! };
        if (deviceType >= 0)
            payload["deviceType"] = deviceType;

        var data = await PostEnvelopeAsync("/get-peq", payload).ConfigureAwait(false);
        var list = Records(data).Select(CloudPreset.FromJson).ToList();
        return new PresetPage(list, list.Count);
    }

    // -- account -------------------------------------------------------------

    private async Task<Json> PostFormAsync(string path, Dictionary<string, string> form, string? bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + UserSystem + path)
        {
            Content = new FormUrlEncodedContent(form)
        };
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            throw new AuthException($"Could not reach the account server: {exc.Message}");
        }

        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!Json.TryParse(text, out var parsed))
            throw new AuthException("The account server sent nothing we could read.");
        if (!response.IsSuccessStatusCode)
        {
            string message = parsed["msg"].AsString
                             ?? parsed["error_description"].AsString
                             ?? "Sign in failed.";
            throw new AuthException(message);
        }
        return parsed;
    }

    private async Task<string> AppTokenAsync()
    {
        if (_appToken is not null)
            return _appToken;

        var payload = await PostFormAsync("/oauth/token", new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["grant_type"] = "client_credentials",
        }, null).ConfigureAwait(false);

        _appToken = payload["access_token"].AsString
                    ?? throw new AuthException("No application token came back.");
        return _appToken;
    }

    /// <summary>
    /// Fetch a picture CAPTCHA. The image is for a person to read - show it,
    /// do not try to solve it.
    /// </summary>
    public async Task<(byte[] Image, string Token)> CaptchaAsync()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, BaseUrl + UserSystem + "/api/portal/captcha/pic")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", await AppTokenAsync().ConfigureAwait(false));

        Json parsed;
        try
        {
            var response = await _http.SendAsync(request).ConfigureAwait(false);
            parsed = Json.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }
        catch (Exception exc)
        {
            throw new AuthException($"Could not fetch a verification image: {exc.Message}");
        }

        var result = parsed["result"];
        string? image = result["basePic"].AsString;
        string? token = result["token"].AsString;
        if (image is null || token is null)
            throw new AuthException(parsed["msg"].AsString ?? "No verification image came back.");

        int comma = image.IndexOf(',');
        if (comma >= 0)
            image = image.Substring(comma + 1);
        return (Convert.FromBase64String(image), token);
    }

    /// <summary>
    /// Exchange the user's own credentials for a token. The password is
    /// forwarded to FiiO and then dropped; nothing here keeps it.
    /// </summary>
    public async Task LoginAsync(string username, string password, string captcha, string captchaToken)
    {
        var payload = await PostFormAsync("/oauth/token", new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = password,
            ["picCaptcha"] = captcha,
            ["captchaToken"] = captchaToken,
        }, await AppTokenAsync().ConfigureAwait(false)).ConfigureAwait(false);

        AccessToken = payload["access_token"].AsString
                      ?? throw new AuthException(payload["msg"].AsString ?? "Sign in failed.");
        await FetchUserInfoAsync().ConfigureAwait(false);
    }

    private async Task FetchUserInfoAsync()
    {
        var key = NewKey();
        var url = BaseUrl + UserSystem + "/api/portal/user/user_info?cipherSign="
                  + Uri.EscapeDataString(RsaWrap(UserKeyPem, key));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        string text;
        try
        {
            var response = await _http.SendAsync(request).ConfigureAwait(false);
            text = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();
        }
        catch (Exception exc)
        {
            throw new AuthException($"Could not read the account profile: {exc.Message}");
        }

        // The profile arrives as a bare AES blob, sometimes inside an envelope.
        string? blob;
        if (!Json.TryParse(text, out var parsed))
            blob = text;
        else if (parsed.IsString)
            blob = parsed.AsString;
        else
            blob = parsed["data"].AsString ?? parsed["result"].AsString;

        if (string.IsNullOrEmpty(blob))
            throw new AuthException("The account profile came back in a shape we do not understand.");

        var info = Json.Parse(AesDecrypt(key, blob!));
        UserId = info["userId"].AsString;
        UserName = info["userName"].AsString ?? "";
        if (UserId is null)
            throw new AuthException("The account profile carried no user id.");
    }

    public void Logout()
    {
        AccessToken = null;
        UserId = null;
        UserName = "";
    }
}
