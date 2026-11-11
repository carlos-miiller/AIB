using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Tools;

/// <summary>
/// Procura um texto DENTRO dos arquivos e devolve arquivo, linha e o trecho.
/// <para>
/// O par do <see cref="GlobTool"/>: um acha pelo nome, o outro pelo conteúdo. Sem os dois, "onde
/// está a função que monta o prompt" vira um comando de shell composto de cabeça, com aspas e
/// escape que o modelo erra — foi o que aconteceu duas vezes seguidas em produção com scripts
/// de PowerShell inline.
/// </para>
/// <para>
/// SOMENTE LEITURA, e devolve o TRECHO, não o arquivo. É a diferença entre gastar vinte tokens e
/// gastar doze mil: o <c>read</c> traz o arquivo inteiro, e quase sempre o que se queria era uma
/// linha.
/// </para>
/// </summary>
public sealed class GrepTool : ITool
{
    /// <summary>Teto de linhas na resposta. Acima disso a resposta vira o problema.</summary>
    public const int TetoDeLinhas = 60;

    /// <summary>Arquivos examinados no máximo. Uma raiz mal escolhida varreria o disco.</summary>
    public const int TetoDeArquivos = 2000;

    /// <summary>Arquivo maior que isto é pulado: é binário ou log, não código nem documento.</summary>
    public const long TetoDoArquivoEmBytes = 2 * 1024 * 1024;

    public string Name => Ferramentas.Buscar;

    /// <summary>
    /// Teto de tempo da varredura, o mesmo do <see cref="GlobTool.Prazo"/>. Ler o conteúdo de
    /// milhares de arquivos é mais caro que listá-los, e não havia parada nenhuma.
    /// </summary>
    public static readonly TimeSpan Prazo = GlobTool.Prazo;

    public string Description =>
        "Procura texto ou expressão regular DENTRO dos arquivos; devolve arquivo:linha: trecho, "
        + $"até {TetoDeLinhas} linhas. Use no lugar de Select-String/findstr. 'glob' filtra por "
        + "nome.";

    public int RequiredLevel => 1;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        functionName: Name,
        functionDescription: Description,
        functionParameters: BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "pattern": {
                    "type": "string",
                    "description": "Texto ou expressão regular a procurar."
                },
                "path": {
                    "type": "string",
                    "description": "Pasta onde procurar. Padrão: a pasta do usuário."
                },
                "glob": {
                    "type": "string",
                    "description": "Filtra os arquivos por nome, como '*.cs'. Padrão: todos."
                },
                "ignore_case": {
                    "type": "boolean",
                    "description": "Ignorar maiúsculas e minúsculas. Padrão false."
                }
            },
            "required": ["pattern"]
        }
        """)
    );

    /// <summary>A pasta de dados do AIB. Nula em produção; os ensaios passam uma temporária.</summary>
    private readonly string? _dados;

    public GrepTool(string? raizDeDados = null) => _dados = raizDeDados;

    private static string RaizPadrao => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private Acesso AcessoDe(string argumentsJson) => DadosProtegidos.Leitura(DadosProtegidos.Caminho(argumentsJson, RaizPadrao), _dados);

    /// <summary>Cofre, configurações e perfil do navegador não são lidos. Ver <see cref="DadosProtegidos"/>.</summary>
    public string? Validar(string argumentsJson) =>
        AcessoDe(argumentsJson) == Acesso.Negado ? DadosProtegidos.Recusa(DadosProtegidos.Caminho(argumentsJson, RaizPadrao)) : null;

    /// <summary>Buscar não pergunta — a não ser dentro da pasta de dados do AIB.</summary>
    public bool PedeConfirmacao(string argumentsJson) => AcessoDe(argumentsJson) == Acesso.Pergunta;

    public CommandConfirmationContext? BuildConfirmationContext(string argumentsJson, int userLevel) =>
        DadosProtegidos.Cartao(Name, "BUSCAR TEXTO EM", DadosProtegidos.Caminho(argumentsJson, RaizPadrao), userLevel);

    public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1) =>
        ExecutarAutorizadoAsync(argumentsJson, userLevel, null);

    /// <summary>Quem chega sem o cartão a uma leitura que pedia cartão é recusado aqui.</summary>
    public Task<string> ExecutarAutorizadoAsync(string argumentsJson, int userLevel, CommandConfirmationContext? autorizado)
    {
        string caminho = DadosProtegidos.Caminho(argumentsJson, RaizPadrao);

        return AcessoDe(argumentsJson) switch
        {
            Acesso.Negado => Task.FromResult(DadosProtegidos.Recusa(caminho)),
            Acesso.Pergunta when autorizado == null => Task.FromResult(DadosProtegidos.SemCartao(caminho)),
            _ => BuscarAsync(argumentsJson)
        };
    }

    private Task<string> BuscarAsync(string argumentsJson)
    {
        string padrao, raiz, mascara;
        bool ignorarCaixa;

        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);

            padrao = (args.TryGetProperty("pattern", out var p) ? p.GetString() : null) ?? "";
            raiz = PathArgumentRepair.Normalize(
                args.TryGetProperty("path", out var d) ? d.GetString() : null);
            mascara = (args.TryGetProperty("glob", out var g) ? g.GetString() : null) ?? "*";
            ignorarCaixa = args.TryGetProperty("ignore_case", out var i)
                           && i.ValueKind == JsonValueKind.True;
        }
        catch (JsonException ex)
        {
            return Task.FromResult($"ERRO: argumentos ilegíveis ({ex.Message}).");
        }

        if (padrao.Length == 0)
            return Task.FromResult("ERRO: o parâmetro 'pattern' é obrigatório.");

        if (raiz.Length == 0)
            raiz = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!Directory.Exists(raiz))
            return Task.FromResult(PreVooDeCaminho.Conferir($"\"{raiz}\"")
                                   ?? $"ERRO: a pasta '{raiz}' não existe.");

        Console.WriteLine($"[TOOL: {Name}] /{padrao}/ em {raiz} ({mascara})");

        return Task.FromResult(Buscar(raiz, padrao, mascara, ignorarCaixa, raizDeDados: _dados));
    }

    /// <summary>
    /// A busca. Pública e pura o bastante para os ensaios: o que importa aqui é o formato da
    /// resposta, e ele é o que o modelo vai ler para decidir o passo seguinte.
    /// </summary>
    public static string Buscar(string raiz, string padrao, string mascara = "*",
                                bool ignorarCaixa = false, TimeSpan? prazo = null,
                                string? raizDeDados = null)
    {
        Regex regex;

        try
        {
            var opcoes = RegexOptions.Compiled
                         | (ignorarCaixa ? RegexOptions.IgnoreCase : RegexOptions.None);

            regex = new Regex(padrao, opcoes, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            // Expressão inválida é o caso comum quando o modelo escreve regex de cabeça. Dizer
            // qual é o problema é o que evita a segunda tentativa às cegas.
            return $"ERRO: expressão regular inválida — {ex.Message}. "
                   + "Para procurar texto literal, escape os caracteres especiais.";
        }

        var linhas = new List<string>();
        int arquivosLidos = 0, arquivosComAcerto = 0, acertos = 0;
        bool parouNoTeto = false, parouNoPrazo = false;

        var relogio = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan teto = prazo ?? Prazo;

        try
        {
            // As mesmas opções do glob, e pelo mesmo motivo: com IgnoreInaccessible = false a
            // primeira pasta protegida do perfil do usuário derrubava a busca inteira.
            foreach (string caminho in Directory.EnumerateFiles(raiz, mascara, GlobTool.Opcoes(recursivo: true)))
            {
                if (arquivosLidos >= TetoDeArquivos) { parouNoTeto = true; break; }
                if (relogio.Elapsed >= teto) { parouNoPrazo = true; break; }

                // Uma busca na pasta do usuário atravessaria ~/.AIB e traria conversas antigas
                // e registros sem ninguém ter autorizado.
                if (!DadosProtegidos.NaVarredura(caminho, raiz, raizDeDados)) continue;

                arquivosLidos++;

                FileInfo info;
                try { info = new FileInfo(caminho); } catch { continue; }
                if (info.Length > TetoDoArquivoEmBytes) continue;

                bool primeiraDoArquivo = true;
                int numero = 0;

                foreach (string linha in LerLinhas(caminho))
                {
                    numero++;
                    if (!regex.IsMatch(linha)) continue;

                    acertos++;
                    if (primeiraDoArquivo) { arquivosComAcerto++; primeiraDoArquivo = false; }

                    if (linhas.Count < TetoDeLinhas)
                        linhas.Add($"{caminho}:{numero}: {Encurtar(linha)}");
                }
            }
        }
        catch (Exception ex)
        {
            return $"ERRO ao buscar: {ex.Message}";
        }

        // Onde a varredura parou é parte da resposta: sem isso, um resultado parcial parece
        // completo, e "não achei" vira uma conclusão falsa sobre o disco.
        string ondeParou =
            parouNoPrazo
                ? $" Parei em {teto.TotalSeconds:0} s — a resposta pode estar incompleta; "
                  + "aponte 'path' para uma pasta mais específica."
                : parouNoTeto
                    ? $" Parei no teto de {TetoDeArquivos} arquivos — a resposta pode estar "
                      + "incompleta; aponte 'path' para uma pasta mais específica ou use 'glob'."
                    : "";

        if (acertos == 0)
            return $"Nada casa com '{padrao}' em '{raiz}' (arquivos: {mascara}). "
                   + $"{arquivosLidos} arquivo(s) examinado(s).{ondeParou}";

        var saida = new List<string>
        {
            $"{acertos} acerto(s) em {arquivosComAcerto} arquivo(s):"
        };

        saida.AddRange(linhas);

        if (acertos > linhas.Count)
            saida.Add($"(+{acertos - linhas.Count} não listado(s); teto de {TetoDeLinhas} linhas — "
                      + "refine o padrão ou o glob)");

        if (ondeParou.Length > 0) saida.Add("(" + ondeParou.Trim() + ")");

        return string.Join("\n", saida);
    }

    /// <summary>
    /// Lê linha a linha, sem carregar o arquivo inteiro. Arquivo ilegível — binário, em uso,
    /// sem permissão — é PULADO: um deles não pode interromper a varredura dos outros mil.
    /// </summary>
    private static IEnumerable<string> LerLinhas(string caminho)
    {
        StreamReader leitor;

        try { leitor = new StreamReader(caminho, detectEncodingFromByteOrderMarks: true); }
        catch { yield break; }

        using (leitor)
        {
            while (true)
            {
                string? linha;
                try { linha = leitor.ReadLine(); }
                catch { yield break; }

                if (linha == null) yield break;

                // Arquivo da memória é cifrado por linha: a busca é no texto, não na cifra.
                yield return ArquivoCifrado.Abrir(linha);
            }
        }
    }

    private static string Encurtar(string linha)
    {
        string t = linha.Trim();
        return t.Length <= 200 ? t : t.Substring(0, 200) + "…";
    }
}
