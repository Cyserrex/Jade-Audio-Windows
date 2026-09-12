using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using JadeAudioControl.Compat;

namespace JadeAudioControl.Cloud;

/// <summary>
/// Keeps a signed-in session across restarts, for "stay signed in".
///
/// Only tokens are stored - never the password, which this app does not hold
/// for longer than the moment it is forwarded to FiiO. The file is encrypted
/// with DPAPI under <see cref="DataProtectionScope.CurrentUser"/>, so it can
/// only be read back by the same Windows account on the same machine: copying
/// it elsewhere yields nothing.
/// </summary>
public sealed class StoredSession
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";

    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;

    /// <summary>True while the token has more than a day left on it.</summary>
    public bool IsComfortablyValid => DateTime.UtcNow < ExpiresAtUtc - TimeSpan.FromDays(1);
}

public static class SessionStore
{
    // Mixed into the DPAPI blob so a file from another app cannot be swapped in.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("JadeAudioControl.Session.v1");

    private static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JadeAudioControl");

    private static string Path_ => Path.Combine(Directory, "session.dat");

    public static bool Exists => File.Exists(Path_);

    public static void Save(StoredSession session)
    {
        var payload = new Dictionary<string, object>
        {
            ["accessToken"] = session.AccessToken,
            ["refreshToken"] = session.RefreshToken,
            ["expiresAtUtc"] = session.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["userId"] = session.UserId,
            ["userName"] = session.UserName,
        };

        var plain = Encoding.UTF8.GetBytes(Json.Write(payload));
        var sealed_ = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllBytes(Path_, sealed_);
    }

    /// <summary>The stored session, or null if there is none or it cannot be read.</summary>
    public static StoredSession? Load()
    {
        if (!Exists)
            return null;

        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(Path_), Entropy, DataProtectionScope.CurrentUser);
            var json = Json.Parse(Encoding.UTF8.GetString(plain));
            var session = new StoredSession
            {
                AccessToken = json["accessToken"].AsString ?? "",
                RefreshToken = json["refreshToken"].AsString ?? "",
                UserId = json["userId"].AsString ?? "",
                UserName = json["userName"].AsString ?? "",
            };
            if (DateTime.TryParse(json["expiresAtUtc"].AsString, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var expires))
                session.ExpiresAtUtc = expires.ToUniversalTime();

            return string.IsNullOrEmpty(session.AccessToken) ? null : session;
        }
        catch (Exception)
        {
            // A blob written by another Windows account, or a corrupt file.
            Clear();
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(Path_))
                File.Delete(Path_);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
