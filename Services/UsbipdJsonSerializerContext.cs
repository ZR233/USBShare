using System.Text.Json.Serialization;
using USBShare.Models;

namespace USBShare.Services;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(UsbipStateSnapshot))]
internal sealed partial class UsbipdJsonSerializerContext : JsonSerializerContext
{
}
