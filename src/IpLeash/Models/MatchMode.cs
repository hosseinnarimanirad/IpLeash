using System.Text.Json.Serialization;

namespace IpLeash.Models;

/// <summary>
/// Which rule decides whether the machine is where it is supposed to be.
///
/// <see cref="ExactIp"/> is the original behaviour and stays the default: settings files written
/// before country locking existed have no value for this, so they load as <see cref="ExactIp"/>
/// and behave exactly as they did.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MatchMode>))]
public enum MatchMode
{
    /// <summary>The public IP must equal one specific address.</summary>
    ExactIp,

    /// <summary>The public IP must geolocate to one specific country, whatever the address.</summary>
    Country,
}
