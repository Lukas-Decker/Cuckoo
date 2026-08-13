using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuckoo.Core;

/// <summary>
/// A persisted GQL query (operation name + SHA256 hash), optionally carrying variables.
/// Mirrors GQLPersistedQuery from DevilXD's original miner.
/// </summary>
public sealed class GqlOperation
{
    public string OperationName { get; }
    private readonly string _sha256;
    private readonly JsonObject? _variables;

    public GqlOperation(string name, string sha256, JsonObject? variables = null)
    {
        OperationName = name;
        _sha256 = sha256;
        _variables = variables;
    }

    /// <summary>Returns a copy of this operation with the given variables merged in.</summary>
    public GqlOperation WithVariables(JsonObject variables)
    {
        JsonObject merged = _variables is null
            ? variables
            : MergeVars((JsonObject)_variables.DeepClone(), variables);
        return new GqlOperation(OperationName, _sha256, merged);
    }

    private static JsonObject MergeVars(JsonObject baseVars, JsonObject vars)
    {
        foreach (var (key, value) in vars)
        {
            if (baseVars[key] is JsonObject baseObj && value is JsonObject valueObj)
                MergeVars(baseObj, valueObj);
            else
                baseVars[key] = value?.DeepClone();
        }
        return baseVars;
    }

    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["operationName"] = OperationName,
            ["extensions"] = new JsonObject
            {
                ["persistedQuery"] = new JsonObject
                {
                    ["version"] = 1,
                    ["sha256Hash"] = _sha256,
                }
            }
        };
        if (_variables is not null)
            obj["variables"] = _variables.DeepClone();
        return obj;
    }
}

/// <summary>
/// A raw GQL query carrying a GZIP+Base64 encoded event payload,
/// used for the "sendSpadeEvents" watch mutation.
/// </summary>
public static class SpadeEvents
{
    private const string Mutation =
        "\n mutation SendEvents($input: SendSpadeEventsInput!) " +
        "{\n sendSpadeEvents(input: $input) {\n statusCode\n}\n}\n";

    public static JsonObject Build(JsonArray events)
    {
        string minified = events.ToJsonString(Utils.MinifiedJson);
        byte[] raw = Encoding.UTF8.GetBytes(minified);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
            gzip.Write(raw);
        string g64Data = Convert.ToBase64String(output.ToArray());

        return new JsonObject
        {
            ["query"] = Mutation,
            ["variables"] = new JsonObject
            {
                ["input"] = new JsonObject
                {
                    ["data"] = g64Data,
                    ["repository"] = "twilight",
                    ["encoding"] = "GZIP_B64",
                }
            }
        };
    }
}
