using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AIB.Services.Ai;

/// <summary>
/// Põe por escrito um corpo de requisição ao modelo — o arquivo do "Imprimir o prompt".
/// <para>
/// Tudo sai do CORPO, nunca de outra fonte: a parte legível é o mesmo JSON aberto em seções —
/// textos com as quebras de linha de verdade, ferramentas e parâmetros recuados —, e o corpo
/// cru vai inteiro no fim, para quem quiser conferir byte a byte.
/// </para>
/// </summary>
public static class RetratoDoEnvio
{
    private static readonly JsonSerializerOptions Legivel = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Renderizar(string corpo, DateTime quando)
    {
        string regua = new('═', 72);
        var sb = new StringBuilder();

        void Secao(string titulo)
        {
            sb.AppendLine();
            sb.AppendLine(regua);
            sb.AppendLine(titulo);
            sb.AppendLine(regua);
        }

        sb.AppendLine($"AIB · simulação do primeiro envio de uma conversa nova · {quando:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("Nada foi enviado ao modelo. Isto é o que iria, com as configurações salvas neste momento.");

        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var raiz = doc.RootElement;

            if (raiz.TryGetProperty("messages", out var mensagens) && mensagens.ValueKind == JsonValueKind.Array)
            {
                int total = mensagens.GetArrayLength();
                int i = 0;

                foreach (var m in mensagens.EnumerateArray())
                {
                    i++;
                    string papel = m.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                        ? r.GetString() ?? "?"
                        : "?";

                    Secao($"MENSAGEM {i}/{total} · {papel}");

                    if (m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                        sb.AppendLine(c.GetString());

                    if (m.TryGetProperty("tool_calls", out var chamadas))
                        sb.AppendLine(JsonSerializer.Serialize(chamadas, Legivel));
                }
            }

            if (raiz.TryGetProperty("tools", out var ferramentas) && ferramentas.ValueKind == JsonValueKind.Array)
            {
                // O Ollama escreve estas definições dentro do prompt, pelo template do modelo:
                // elas custam prefill como qualquer texto acima.
                Secao($"FERRAMENTAS · {ferramentas.GetArrayLength()}");
                sb.AppendLine(JsonSerializer.Serialize(ferramentas, Legivel));
            }

            Secao("PARÂMETROS");
            foreach (var campo in raiz.EnumerateObject())
            {
                if (campo.Name is "messages" or "tools") continue;
                sb.AppendLine($"{campo.Name}: {JsonSerializer.Serialize(campo.Value, Legivel)}");
            }
        }
        catch (JsonException ex)
        {
            // Não deveria acontecer — o corpo foi o próprio JsonSerializer que escreveu. Se
            // acontecer, o cru abaixo continua sendo a verdade, e perdê-lo seria pior.
            sb.AppendLine();
            sb.AppendLine($"(não consegui abrir o corpo em seções: {ex.Message})");
        }

        Secao("CORPO EXATO · JSON cru, como iria");
        sb.AppendLine(corpo);

        return sb.ToString();
    }

    /// <summary>
    /// Grava em <paramref name="pasta"/> e devolve o caminho. <c>prompt-*.txt</c>, e não
    /// <c>execucao-*.log</c>: a poda do registro de execução apaga por esse padrão, e o arquivo
    /// não pode sumir por causa de um log que ele não é.
    /// </summary>
    public static string Gravar(string texto, string pasta, DateTime quando)
    {
        Directory.CreateDirectory(pasta);
        string caminho = Path.Combine(pasta, $"prompt-{quando:yyyy-MM-dd-HHmmss}.txt");
        File.WriteAllText(caminho, texto, new UTF8Encoding(false));
        return caminho;
    }
}
