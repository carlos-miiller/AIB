using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIB.Services.Memory;

/// <summary>
/// Os fatos duráveis: <c>~/.AIB/memory/facts.md</c>, mais o registro do que já foi promovido.
/// <para>
/// Fica na RAIZ da memória, e não dentro de uma sessão, porque é a única faixa que atravessa
/// conversas. Capítulo e ato morrem com a sessão; fato é o que sobra depois dela.
/// </para>
/// <para>
/// São dois arquivos com papéis opostos, e a separação é deliberada:
/// <list type="bullet">
/// <item><description><c>facts.md</c> — do USUÁRIO. Ele edita, reordena e apaga, pela aba
/// Memória das configurações (<see cref="Regravar"/>): o arquivo é cifrado por linha
/// (<see cref="ArquivoCifrado"/>) e não abre mais no bloco de notas. A AIB só acrescenta linhas
/// no fim; nunca reescreve nem remove nada que esteja lá.</description></item>
/// <item><description><c>facts.index.jsonl</c> — da MÁQUINA. Append-only, guarda a chave de tudo
/// que já foi promovido alguma vez.</description></item>
/// </list>
/// Sem o segundo arquivo, um fato que o usuário apagasse por estar errado voltaria sozinho na
/// próxima promoção — a memória discutindo com o dono dela.
/// </para>
/// </summary>
public sealed class FactStore
{
    /// <summary>Sem BOM: o arquivo é markdown para o usuário abrir, não binário.</summary>
    private static readonly UTF8Encoding SemBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private const string Cabecalho =
        """
        # Fatos duráveis

        <!--
        A AIB acrescenta linhas aqui quando algo se repete o bastante para valer memória
        permanente. Você pode editar, reordenar ou apagar qualquer linha em Configurações >
        Memória: nada é reescrito automaticamente, e o que você apagar não volta.

        Só linhas que começam com "- " entram no prompt. As de cima têm prioridade quando não
        cabe tudo, então ponha o que mais importa no começo.
        -->

        """;

    private readonly object _gate = new();
    private readonly string _root;

    /// <param name="rootOverride">Raiz alternativa. Existe para o teste não escrever no ~/.AIB real.</param>
    public FactStore(string? rootOverride = null) =>
        _root = rootOverride ?? RaizPadrao ?? DirectoryService.MemoryDir;

    /// <summary>
    /// Troca a raiz de quem não passa uma. Só a suíte usa: a aba Memória cria o seu
    /// <c>FactStore</c> sem raiz, e uma tela de ensaio leria os fatos de verdade do usuário.
    /// </summary>
    public static string? RaizPadrao { get; set; }

    public string FactsPath => Path.Combine(_root, "facts.md");

    /// <summary>Registro do que já foi promovido. Não é para leitura humana.</summary>
    public string LedgerPath => Path.Combine(_root, "facts.index.jsonl");

    /// <summary>
    /// As linhas de fato do arquivo, em ordem. Cabeçalhos, comentários e linhas em branco são
    /// ignorados — um markdown editado à mão tem de tolerar o que o usuário escrever nele.
    /// </summary>
    public IReadOnlyList<string> ReadFacts()
    {
        try
        {
            if (!File.Exists(FactsPath)) return Array.Empty<string>();

            return ArquivoCifrado.Linhas(FactsPath)
                .Select(l => l.TrimEnd())
                .Where(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Length > 2)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Falha ao ler facts.md: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>O arquivo inteiro em texto claro, como o usuário o vê na aba Memória. Vazio sem arquivo.</summary>
    public string Texto()
    {
        try { return string.Join(Environment.NewLine, ArquivoCifrado.Linhas(FactsPath)); }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Falha ao ler facts.md: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Troca o arquivo pelo texto que o usuário editou na aba Memória. É a edição à mão de
    /// antes, agora que o arquivo é cifrado. O índice não muda: o que ele apagou não volta.
    /// Por um temporário — uma queda no meio deixa o arquivo anterior.
    /// </summary>
    public bool Regravar(string texto)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_root);
                string temporario = FactsPath + ".tmp";
                if (File.Exists(temporario)) File.Delete(temporario);
                ArquivoCifrado.Acrescentar(temporario, texto ?? "");
                if (!File.Exists(temporario)) File.WriteAllText(temporario, "", SemBom);
                File.Move(temporario, FactsPath, overwrite: true);
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Falha ao regravar facts.md: {ex.Message}");
            return false;
        }
    }

    /// <summary>Chaves já promovidas alguma vez, incluindo as de fatos que o usuário apagou.</summary>
    public HashSet<string> ReadPromotedKeys()
    {
        var chaves = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            if (!File.Exists(LedgerPath)) return chaves;

            foreach (var linha in ArquivoCifrado.Linhas(LedgerPath))
            {
                if (string.IsNullOrWhiteSpace(linha)) continue;
                try
                {
                    var registro = JsonSerializer.Deserialize<FactRecord>(linha, Json);
                    if (registro?.Key != null) chaves.Add(registro.Key);
                }
                catch (JsonException)
                {
                    // Linha truncada por queda no meio da escrita. Perder uma chave só faz o
                    // fato poder ser promovido de novo; perder o arquivo inteiro seria pior.
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Falha ao ler o registro de fatos: {ex.Message}");
        }

        return chaves;
    }

    /// <summary>
    /// Grava os candidatos que ainda não foram promovidos. Devolve quantos entraram.
    /// <para>
    /// Nunca lança: promoção é acréscimo, e um acréscimo que derruba a conversa não vale.
    /// </para>
    /// </summary>
    public int Promote(IEnumerable<FactCandidate>? candidatos)
    {
        if (candidatos == null) return 0;

        try
        {
            lock (_gate)
            {
                var jaPromovidos = ReadPromotedKeys();

                var novos = candidatos
                    .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Key))
                    .Where(c => jaPromovidos.Add(c.Key))   // Add devolve false no repetido
                    .ToList();

                if (novos.Count == 0) return 0;

                Directory.CreateDirectory(_root);

                if (!File.Exists(FactsPath))
                    ArquivoCifrado.Acrescentar(FactsPath, Cabecalho);

                var texto = new StringBuilder();
                foreach (var fato in novos) texto.Append(fato.Line.TrimEnd()).Append(Environment.NewLine);
                ArquivoCifrado.Acrescentar(FactsPath, texto.ToString());

                string agora = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                var registro = new StringBuilder();
                foreach (var fato in novos)
                    registro.Append(JsonSerializer.Serialize(new FactRecord(fato.Key, fato.Line, agora), Json))
                            .Append(Environment.NewLine);
                ArquivoCifrado.Acrescentar(LedgerPath, registro.ToString());

                return novos.Count;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Falha ao promover fatos: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Bloco de fatos para o prompt, dentro da cota.
    /// <para>
    /// O corte é do FIM para o começo, ao contrário dos capítulos: em capítulo o que importa é
    /// o recente, em fato o que importa é a ordem que o usuário escolheu. O topo do arquivo é
    /// onde ele põe o que não pode ser esquecido.
    /// </para>
    /// </summary>
    public static string Render(IReadOnlyList<string>? facts, MemoryQuota quota, TokenCounter counter)
    {
        if (facts == null || facts.Count == 0 || quota.Facts <= 0) return "";

        var texto = new StringBuilder();
        texto.Append("## Fatos duráveis\n");

        int cabecalho = counter.CountText(texto.ToString());
        int gasto = cabecalho;
        int entraram = 0;

        foreach (var gravado in facts)
        {
            string fato = ArtifactDigest.ParaOPrompt(gravado);
            int custo = counter.CountText(fato + "\n");
            if (gasto + custo > quota.Facts) break;

            texto.Append(fato).Append('\n');
            gasto += custo;
            entraram++;
        }

        return entraram == 0 ? "" : texto.ToString();
    }
}

/// <param name="Key">Chave estável do fato, vinda do <see cref="ArtifactDigest.KeyOf"/>.</param>
/// <param name="Line">Linha gravada no facts.md no momento da promoção.</param>
/// <param name="AtUtc">Quando foi promovido.</param>
public sealed record FactRecord(string Key, string Line, string AtUtc);
