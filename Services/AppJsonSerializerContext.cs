using System.Text.Json.Serialization;
using USBShare.Models;

namespace USBShare.Services;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(UsbipStateSnapshot))]
internal sealed partial class AppJsonSerializerContext : JsonSerializerContext
{
}
