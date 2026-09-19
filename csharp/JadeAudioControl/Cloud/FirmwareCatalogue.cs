using System;
using System.Net.Http;
using System.Threading.Tasks;
using JadeAudioControl.Compat;

namespace JadeAudioControl.Cloud;

public enum FirmwareState
{
    /// <summary>No entry for this device, or the list could not be reached.</summary>
    Unknown,
    UpToDate,
    UpdateAvailable,
    /// <summary>Newer than anything the list knows about.</summary>
    Ahead,
}

/// <summary>What the catalogue knows about one device's firmware.</summary>
public sealed class FirmwareInfo
{
    public string Product { get; set; } = "";
    public string Latest { get; set; } = "";
    public int Major { get; set; } = -1;
    public int Minor { get; set; } = -1;
    public string Url { get; set; } = "";
    public string InstructionsUrl { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class FirmwareCheck
{
    public FirmwareState State { get; set; } = FirmwareState.Unknown;
    public string Installed { get; set; } = "";
    public FirmwareInfo? Info { get; set; }
    public string Message { get; set; } = "";
    public string FallbackUrl { get; set; } = "https://www.fiio.com/supports";

    /// <summary>The best page to send someone to, whatever the outcome.</summary>
    public string BestUrl =>
        !string.IsNullOrEmpty(Info?.Url) ? Info!.Url : FallbackUrl;
}

/// <summary>
/// Checks the installed firmware against a published list.
///
/// FiiO exposes no version API for USB dongles - their web app only carries the
/// Qualcomm OTA flow, which is for Bluetooth models - so the list is a JSON file
/// kept in this repository and read from the main branch at runtime. Adding a
/// release is a commit, not a new build of the app.
///
/// Nothing here writes to the device. Flashing a dongle is how a dongle gets
/// bricked, and FiiO ships their own tool for it; this only tells you whether
/// there is something newer and where to get it.
/// </summary>
public static class FirmwareCatalogue
{
    public const string CatalogueUrl =
        "https://raw.githubusercontent.com/Cyserrex/Jade-Audio-Windows/main/firmware.json";

    public static async Task<FirmwareCheck> CheckAsync(
        HttpClient http, string product, int installedMajor, int installedMinor)
    {
        string installed = Describe(installedMajor, installedMinor);
        var result = new FirmwareCheck { Installed = installed };

        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, CatalogueUrl);
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true,
            };
            var response = await http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                result.Message = $"Could not read the firmware list (HTTP {(int)response.StatusCode}).";
                return result;
            }
            body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            result.Message = "The firmware list took too long to answer. Try again.";
            return result;
        }
        catch (Exception exc)
        {
            result.Message = $"Could not reach the firmware list: {exc.Message}";
            return result;
        }

        var json = Json.Parse(body);
        if (!string.IsNullOrEmpty(json["fallbackUrl"].AsString))
            result.FallbackUrl = json["fallbackUrl"].AsString!;

        foreach (var entry in json["devices"].Items)
        {
            if (!string.Equals(entry["product"].AsString, product, StringComparison.OrdinalIgnoreCase))
                continue;

            result.Info = new FirmwareInfo
            {
                Product = entry["product"].AsString ?? product,
                Latest = entry["latest"].AsString ?? "",
                Major = entry["major"].AsInt(-1),
                Minor = entry["minor"].AsInt(-1),
                Url = entry["url"].AsString ?? "",
                InstructionsUrl = entry["instructionsUrl"].AsString ?? "",
                Notes = entry["notes"].AsString ?? "",
            };
            break;
        }

        if (result.Info is null)
        {
            result.Message = $"The list has no entry for {product} yet.";
            return result;
        }

        if (installedMajor < 0 || result.Info.Major < 0)
        {
            result.Message = "The installed version could not be read for comparison.";
            return result;
        }

        int installedValue = (installedMajor * 1000) + installedMinor;
        int latestValue = (result.Info.Major * 1000) + result.Info.Minor;

        if (installedValue < latestValue)
        {
            result.State = FirmwareState.UpdateAvailable;
            result.Message = $"Firmware {result.Info.Latest} is available. You have {installed}.";
        }
        else if (installedValue > latestValue)
        {
            result.State = FirmwareState.Ahead;
            result.Message =
                $"You have {installed}, newer than the {result.Info.Latest} this list knows about.";
        }
        else
        {
            result.State = FirmwareState.UpToDate;
            result.Message = $"Up to date - {installed} is the latest firmware.";
        }
        return result;
    }

    /// <summary>
    /// Render the two version bytes the way FiiO writes them: 2 and 20 is the
    /// "V2.2" in their announcements, so the trailing zero goes.
    /// </summary>
    public static string Describe(int major, int minor)
    {
        if (major < 0)
            return "unknown";
        string text = $"{major}.{minor:00}";
        return text.EndsWith("0", StringComparison.Ordinal) ? text.Substring(0, text.Length - 1) : text;
    }
}
