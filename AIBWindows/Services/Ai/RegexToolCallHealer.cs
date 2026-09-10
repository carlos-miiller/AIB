using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAI.Chat;

namespace AIB.Services.Ai;

/// <summary>
/// Implementação do healer por regex. Cura a alucinação de modelos pequenos que escrevem
/// a chamada de ferramenta como prosa ("Action: read(caminho.txt)") em vez de usar
/// function calling. Recurso INTENCIONAL (doc 03 §3.1).
///
/// Três garantias que o fallback antigo não dava:
///   1. só analisa texto do canal FINAL — conteúdo de &lt;think&gt; nunca vira execução;
///   2. os argumentos são SEMPRE serializados por JsonSerializer, nunca por interpolação;
///   3. os nomes de parâmetro vêm do schema real da ferramenta, nunca são inventados.
/// </summary>
public sealed class RegexToolCallHealer : IToolCallHealer
{
    // Padrão preservado: Action: nome_tool(...) / Ação: nome_tool(...)
    private static readonly Regex TextActionRegex = new(
        @"(?:Action|Ação|action):\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*\(([^)]*)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Pares nome=valor / nome: valor, com valor opcionalmente entre aspas.
    private static readonly Regex NamedArgRegex = new(
        @"([a-zA-Z_][a-zA-Z0-9_]*)\s*[:=]\s*(?:""([^""]*)""|'([^']*)'|([^,]*))",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public bool TryHeal(string finalChannelText, IReadOnlyList<ChatTool> activeTools, out HealedToolCall healed)
    {
        healed = null!;

        if (string.IsNullOrWhiteSpace(finalChannelText) || activeTools == null || activeTools.Count == 0)
            return false;

        var match = TextActionRegex.Match(finalChannelText);
        if (!match.Success) return false;

        string calledName = match.Groups[1].Value;
        var tool = activeTools.FirstOrDefault(t =>
            string.Equals(t.FunctionName, calledName, StringComparison.OrdinalIgnoreCase));
        if (tool == null) return false;

        if (!TryReadSchema(tool, out var schema)) return false;

        string rawArgs = match.Groups[2].Value.Trim();
        if (!TryBindArguments(rawArgs, schema, out var arguments)) return false;

        healed = new HealedToolCall(
            tool.FunctionName,
            JsonSerializer.Serialize(arguments, SerializerOptions),
            match.Value);
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Schema real da ferramenta
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Nomes declarados (em ordem), tipo de cada um e a lista de obrigatórios.</summary>
    private sealed class ToolSchema
    {
        public List<string> Properties { get; } = new();
        public Dictionary<string, string> Types { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Required { get; } = new();

        public bool TryResolveName(string candidate, out string actual)
        {
            foreach (var p in Properties)
            {
                if (string.Equals(p, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    actual = p;
                    return true;
                }
            }
            actual = "";
            return false;
        }
    }

    private static bool TryReadSchema(ChatTool tool, out ToolSchema schema)
    {
        schema = new ToolSchema();
        if (tool.FunctionParameters == null) return true; // ferramenta sem parâmetros

        try
        {
            using var doc = JsonDocument.Parse(tool.FunctionParameters.ToString());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in props.EnumerateObject())
                {
                    schema.Properties.Add(p.Name);
                    string type = "string";
                    if (p.Value.ValueKind == JsonValueKind.Object &&
                        p.Value.TryGetProperty("type", out var t) &&
                        t.ValueKind == JsonValueKind.String)
                    {
                        type = t.GetString() ?? "string";
                    }
                    schema.Types[p.Name] = type;
                }
            }

            if (root.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in req.EnumerateArray())
                {
                    if (r.ValueKind == JsonValueKind.String)
                    {
                        var name = r.GetString();
                        if (!string.IsNullOrEmpty(name)) schema.Required.Add(name!);
                    }
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Ligação dos argumentos capturados ao schema
    // ─────────────────────────────────────────────────────────────────────────

    private static bool TryBindArguments(string rawArgs, ToolSchema schema, out Dictionary<string, object?> arguments)
    {
        arguments = new Dictionary<string, object?>();

        // 1. Sem argumentos: só vale se a ferramenta não exigir nada.
        if (rawArgs.Length == 0)
            return schema.Required.Count == 0;

        // 2. O modelo escreveu um objeto JSON: reserializa mantendo só o que existe no schema.
        if (rawArgs.StartsWith("{", StringComparison.Ordinal) && TryBindFromJsonObject(rawArgs, schema, arguments))
            return true;
        if (rawArgs.StartsWith("{", StringComparison.Ordinal))
            return false;

        // 3. Pares nome=valor / nome: valor, todos presentes no schema.
        if (TryBindFromNamedPairs(rawArgs, schema, arguments))
            return true;

        // 4. Valor solto: liga ao primeiro obrigatório (ou ao primeiro declarado).
        return TryBindScalar(rawArgs, schema, arguments);
    }

    private static bool TryBindFromJsonObject(string rawArgs, ToolSchema schema, Dictionary<string, object?> arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawArgs);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (schema.TryResolveName(p.Name, out var actual))
                    arguments[actual] = p.Value.Clone();
            }
        }
        catch (JsonException)
        {
            return false;
        }

        foreach (var req in schema.Required)
        {
            if (!arguments.ContainsKey(req)) return false;
        }
        return true;
    }

    private static bool TryBindFromNamedPairs(string rawArgs, ToolSchema schema, Dictionary<string, object?> arguments)
    {
        var matches = NamedArgRegex.Matches(rawArgs);
        if (matches.Count == 0) return false;

        // O que sobra fora dos pares só pode ser separador — senão não é uma lista de pares.
        int cursor = 0;
        foreach (Match m in matches)
        {
            string gap = rawArgs.Substring(cursor, m.Index - cursor);
            if (gap.Any(c => c != ',' && !char.IsWhiteSpace(c))) return false;
            cursor = m.Index + m.Length;
        }
        if (rawArgs.Substring(cursor).Any(c => c != ',' && !char.IsWhiteSpace(c))) return false;

        var bound = new Dictionary<string, object?>();
        foreach (Match m in matches)
        {
            if (!schema.TryResolveName(m.Groups[1].Value, out var actual)) return false;

            string value = m.Groups[2].Success ? m.Groups[2].Value
                         : m.Groups[3].Success ? m.Groups[3].Value
                         : m.Groups[4].Value.Trim();
            bound[actual] = Coerce(value, schema.Types.TryGetValue(actual, out var t) ? t : "string");
        }

        foreach (var req in schema.Required)
        {
            if (!bound.ContainsKey(req)) return false;
        }

        foreach (var kv in bound) arguments[kv.Key] = kv.Value;
        return true;
    }

    private static bool TryBindScalar(string rawArgs, ToolSchema schema, Dictionary<string, object?> arguments)
    {
        // Um valor solto só serve se a ferramenta não exigir mais de um obrigatório.
        if (schema.Required.Count > 1) return false;

        string target;
        if (schema.Required.Count == 1)
        {
            target = schema.TryResolveName(schema.Required[0], out var actual) ? actual : schema.Required[0];
        }
        else if (schema.Properties.Count > 0)
        {
            target = schema.Properties[0];
        }
        else
        {
            return false; // schema não declara parâmetro algum — não há onde ligar o valor
        }

        arguments[target] = Coerce(StripQuotes(rawArgs), schema.Types.TryGetValue(target, out var t) ? t : "string");
        return true;
    }

    private static string StripQuotes(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value.Substring(1, value.Length - 2);
        }
        return value;
    }

    /// <summary>Converte o texto capturado para o tipo declarado no schema; na dúvida, string.</summary>
    private static object? Coerce(string value, string schemaType)
    {
        switch (schemaType.ToLowerInvariant())
        {
            case "integer":
                if (long.TryParse(value, out var l)) return l;
                return value;
            case "number":
                if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
                return value;
            case "boolean":
                if (bool.TryParse(value, out var b)) return b;
                return value;
            default:
                return value;
        }
    }
}
