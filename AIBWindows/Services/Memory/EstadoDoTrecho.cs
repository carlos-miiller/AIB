using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAI.Chat;

namespace AIB.Services.Memory;

/// <summary>
/// Como um alvo terminou o trecho: um arquivo, uma pasta ou um comando.
/// </summary>
/// <param name="Alvo">Caminho absoluto, ou a linha de comando para <see cref="TipoComando"/>.</param>
/// <param name="Tipo"><see cref="TipoArquivo"/>, <see cref="TipoPasta"/> ou <see cref="TipoComando"/>.</param>
/// <param name="Situacao">A frase do desfecho: "criado pelo agente", "apagado junto com a pasta"…</param>
/// <param name="Detalhe">Complemento curto: tamanho, linhas trocadas, primeira linha do erro.</param>
/// <param name="Turno">Índice do turno do último evento (numeração do registro).</param>
/// <param name="Vezes">Quantas vezes o comando rodou no trecho. 1 para arquivos.</param>
public sealed record ItemDeEstado(string Alvo, string Tipo, string Situacao, string? Detalhe, int Turno, int Vezes = 1)
{
    public const string TipoArquivo = "arquivo";
    public const string TipoPasta = "pasta";
    public const string TipoComando = "comando";
}

/// <summary>
/// O ESTADO em que o trecho deixou o mundo, montado por código a partir das ferramentas.
/// <para>
/// Substitui a lista cronológica de artefatos no prompt. A lista dizia "gravou thais_araujo.html"
/// para um arquivo que três turnos depois foi apagado com a pasta, e registrava onze leituras e
/// listagens entre dez ações que mudaram algo. Um modelo com só essa lista não sabia o que
/// existia agora. Medido pela Factory.ai: rastro de arquivos é a pior nota de todos os
/// resumidores por LLM (2,19–2,45 de 5) — por isso sai do modelo e fica com o código.
/// </para>
/// <para>
/// O que ele NÃO afirma é tão importante quanto o que afirma. Um comando arbitrário
/// (<c>powershell -File gerar.ps1</c>) não diz o que mudou no disco: entra como comando, com o
/// desfecho, e "efeitos em disco não rastreados". Apagar só conta com alvo literal absoluto —
/// glob, variável e caminho relativo não viram "apagado". E tudo vale "no fim do capítulo": o
/// disco pode ter mudado depois, e conferir na hora mudaria o texto a cada turno e quebraria o
/// cache do prompt.
/// </para>
/// </summary>
public sealed record EstadoDoTrecho(
    IReadOnlyList<ItemDeEstado> Itens,
    IReadOnlyList<string> Consultados,
    int Consultas)
{
    /// <summary>Teto de itens no prompt. O resto vira "e mais N".</summary>
    public const int TetoDeItens = 20;

    /// <summary>Teto de nomes na linha Consultados.</summary>
    public const int TetoDeConsultados = 12;

    public static readonly EstadoDoTrecho Vazio = new(Array.Empty<ItemDeEstado>(), Array.Empty<string>(), 0);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool EstaVazio => Itens.Count == 0 && Consultas == 0;

    // ─────────────────────────────────────────────────────────────────────────
    // Montagem
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verbos de shell que só olham. O comando que começa por um deles vai para Consultados, e não
    /// para o Estado: um <c>dir</c> repetido cinco vezes era a metade da lista de artefatos.
    /// </summary>
    private static readonly Regex SoLeitura = new(
        @"^\s*(dir|ls|gci|get-childitem|get-content|gc|cat|type|select-string|sls|test-path|get-item|gi|get-itemproperty|get-location|pwd|resolve-path|get-date|get-process|ps|get-service|where|where\.exe|echo|write-output|write-host|get-command|gcm|hostname|whoami|ipconfig|systeminfo|tree|findstr|measure-object|get-filehash|get-acl)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TemCuringa = new(@"[*?]|\$", RegexOptions.Compiled);

    /// <summary>
    /// Percorre os turnos em ordem e dobra cada ação no seu alvo. Parte do estado anterior —
    /// vazio num capítulo, o do ato anterior numa fusão —, então apagar uma pasta marca também
    /// o que um trecho ANTERIOR tinha criado dentro dela.
    /// </summary>
    public static EstadoDoTrecho Montar(IReadOnlyList<Turn> turnos, EstadoDoTrecho? anterior = null)
    {
        var itens = new Dictionary<string, ItemDeEstado>(StringComparer.OrdinalIgnoreCase);
        var ordem = new List<string>();
        var consultados = new List<string>();
        int consultas = anterior?.Consultas ?? 0;

        if (anterior != null)
        {
            foreach (var item in anterior.Itens) Guardar(item);
            consultados.AddRange(anterior.Consultados);
        }

        // Comando que muda o disco sem dizer o quê. Depois de um, o arquivo que aparece numa
        // leitura pode ter sido gerado por ele — e não ser "já existia".
        bool houveComandoOpaco = anterior?.Itens.Any(i => i.Tipo == ItemDeEstado.TipoComando) ?? false;

        foreach (var turno in turnos ?? Array.Empty<Turn>())
        {
            foreach (var (nome, args, resultado) in Pares(turno.Messages))
            {
                bool falhou = ArtifactExtractor.Falhou(resultado);
                bool recusado = ArtifactExtractor.Recusado(resultado);

                switch (nome)
                {
                    case Ferramentas.Ler:
                    {
                        string caminho = ArtifactExtractor.ResumirArgumento(nome, args);
                        if (caminho.Length == 0) break;
                        consultas++;

                        if (falhou)
                        {
                            if (!itens.ContainsKey(caminho))
                                Guardar(new ItemDeEstado(caminho, ItemDeEstado.TipoArquivo, "não encontrado ao ler", Primeira(resultado), turno.Index));
                            break;
                        }

                        bool pasta = resultado.Contains("é uma PASTA", StringComparison.Ordinal);
                        Consultar(consultados, caminho);

                        if (!itens.TryGetValue(caminho, out var existente))
                        {
                            // A leitura é prova de existência NAQUELE momento. Sem gravação
                            // conhecida antes, ou já existia, ou um comando o gerou.
                            if (!pasta)
                                Guardar(new ItemDeEstado(caminho, ItemDeEstado.TipoArquivo,
                                    houveComandoOpaco ? "existia (ou foi gerado por comando)" : "já existia", null, turno.Index));
                        }
                        else if (existente.Situacao.StartsWith("apagad", StringComparison.OrdinalIgnoreCase)
                                 || existente.Situacao.StartsWith("não encontrado", StringComparison.Ordinal))
                        {
                            Guardar(existente with { Situacao = "reapareceu (gerado por comando)", Detalhe = null, Turno = turno.Index });
                        }
                        break;
                    }

                    case Ferramentas.Gravar:
                    case Ferramentas.Editar:
                    {
                        string caminho = ArtifactExtractor.ResumirArgumento(nome, args);
                        if (caminho.Length == 0) break;
                        itens.TryGetValue(caminho, out var antes);
                        bool gravar = nome == Ferramentas.Gravar;

                        if (recusado)
                        {
                            Guardar((antes ?? new ItemDeEstado(caminho, ItemDeEstado.TipoArquivo, "não tocado", null, turno.Index))
                                with { Detalhe = (gravar ? "gravação" : "edição") + " recusada pelo usuário", Turno = turno.Index });
                            break;
                        }

                        if (falhou)
                        {
                            Guardar((antes ?? new ItemDeEstado(caminho, ItemDeEstado.TipoArquivo, "não tocado", null, turno.Index))
                                with { Detalhe = (gravar ? "gravação" : "edição") + " falhou: " + Primeira(resultado), Turno = turno.Index });
                            break;
                        }

                        var artefato = ArtifactExtractor.Construir(nome, args, resultado);
                        string situacao = gravar
                            ? antes == null ? "criado pelo agente"
                              : antes.Situacao.StartsWith("apagad", StringComparison.OrdinalIgnoreCase) ? "recriado pelo agente"
                              : "reescrito pelo agente"
                            : "editado pelo agente";

                        // Editar o que o agente criou continua sendo "criado pelo agente": é a
                        // ORIGEM que o próximo turno precisa saber.
                        if (!gravar && antes != null && antes.Situacao.Contains("pelo agente", StringComparison.Ordinal))
                            situacao = antes.Situacao;

                        Guardar(new ItemDeEstado(caminho, ItemDeEstado.TipoArquivo, situacao, artefato?.Detail, turno.Index));
                        break;
                    }

                    case Ferramentas.Shell:
                    {
                        string comando = ArtifactExtractor.ResumirArgumento(nome, args).Trim();
                        if (comando.Length == 0) break;

                        if (recusado)
                        {
                            Guardar(new ItemDeEstado(comando, ItemDeEstado.TipoComando, "recusado pelo usuário", null, turno.Index));
                            break;
                        }

                        if (ComandoQueApaga.Eh(comando))
                        {
                            Apagar(comando, falhou, resultado, turno.Index);
                            break;
                        }

                        if (SoLeitura.IsMatch(comando))
                        {
                            consultas++;
                            if (falhou)
                                Guardar(new ItemDeEstado(comando, ItemDeEstado.TipoComando, "consulta falhou", Primeira(resultado), turno.Index));
                            break;
                        }

                        houveComandoOpaco = true;
                        ComandoOpaco(comando, falhou, resultado, turno.Index);
                        break;
                    }

                    case Ferramentas.Habilidade:
                    {
                        string chamada = ArtifactExtractor.ResumirArgumento(nome, args).Trim();
                        if (chamada.Length == 0) break;
                        if (recusado)
                        {
                            Guardar(new ItemDeEstado("skill " + chamada, ItemDeEstado.TipoComando, "recusado pelo usuário", null, turno.Index));
                            break;
                        }
                        ComandoOpaco("skill " + chamada, falhou, resultado, turno.Index);
                        break;
                    }

                    case Ferramentas.Procurar:
                    case Ferramentas.Buscar:
                    case Ferramentas.Email:
                    case Ferramentas.LerEmail:
                        consultas++;
                        break;
                }
            }
        }

        return new EstadoDoTrecho(ordem.Select(k => itens[k]).ToList(), consultados, consultas);

        void Guardar(ItemDeEstado item)
        {
            string chave = item.Tipo == ItemDeEstado.TipoComando ? "cmd:" + item.Alvo : item.Alvo.TrimEnd('\\', '/');
            if (itens.ContainsKey(chave)) ordem.Remove(chave);
            itens[chave] = item;
            ordem.Add(chave);
        }

        void ComandoOpaco(string comando, bool falhou, string resultado, int turno)
        {
            itens.TryGetValue("cmd:" + comando, out var antes);
            Guardar(new ItemDeEstado(comando, ItemDeEstado.TipoComando,
                falhou ? "FALHOU" : "ok",
                falhou ? Primeira(resultado) : null,
                turno,
                (antes?.Vezes ?? 0) + 1));
        }

        void Apagar(string comando, bool falhou, string resultado, int turno)
        {
            string? alvo = ComandoQueApaga.Alvo(comando);

            // Sem alvo literal e absoluto não há o que afirmar: fica o fato de que um comando que
            // apaga rodou, sem a linha pronta para repetir.
            if (alvo == null || TemCuringa.IsMatch(alvo) || !Path.IsPathRooted(alvo))
            {
                Guardar(new ItemDeEstado("(comando que apaga arquivos)", ItemDeEstado.TipoComando,
                    falhou ? "FALHOU" : "rodou — alvo não identificado; não repetir sem o usuário pedir",
                    falhou ? Primeira(resultado) : null, turno));
                return;
            }

            alvo = alvo.TrimEnd('\\', '/');

            if (falhou)
            {
                itens.TryGetValue(alvo, out var antes);
                Guardar((antes ?? new ItemDeEstado(alvo, ItemDeEstado.TipoPasta, "não tocado", null, turno))
                    with { Detalhe = "tentativa de apagar falhou: " + Primeira(resultado), Turno = turno });
                return;
            }

            itens.TryGetValue(alvo, out var jaVisto);
            string tipo = jaVisto?.Tipo ?? (Path.HasExtension(alvo) ? ItemDeEstado.TipoArquivo : ItemDeEstado.TipoPasta);
            int vezes = jaVisto != null && jaVisto.Situacao.StartsWith("APAGAD", StringComparison.OrdinalIgnoreCase) ? jaVisto.Vezes + 1 : 1;

            Guardar(new ItemDeEstado(alvo, tipo, "APAGADO pelo agente — já feito, não repetir sem o usuário pedir", null, turno, vezes));

            // Tudo o que se sabe estar DENTRO vai junto. Só o que já apareceu: o que nunca foi
            // visto não pode ser listado, e inventar a lista seria o erro que isto corrige.
            string prefixo = alvo + Path.DirectorySeparatorChar;
            foreach (var chave in itens.Keys.Where(k => k.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var dentro = itens[chave];
                if (dentro.Tipo == ItemDeEstado.TipoComando) continue;
                Guardar(dentro with { Situacao = "apagado junto com a pasta", Detalhe = null, Turno = turno });
            }
        }
    }

    /// <summary>
    /// O estado de vários trechos em sequência, como se fossem um: o mais recente de cada alvo
    /// vale, e uma pasta apagada num trecho posterior leva junto o que um trecho anterior tinha
    /// deixado dentro dela. É assim que o ato é montado — por código, e não resumindo resumos.
    /// </summary>
    public static EstadoDoTrecho Fundir(IEnumerable<EstadoDoTrecho?> estados)
    {
        var itens = new Dictionary<string, ItemDeEstado>(StringComparer.OrdinalIgnoreCase);
        var ordem = new List<string>();
        var consultados = new List<string>();
        int consultas = 0;

        foreach (var estado in estados)
        {
            if (estado == null) continue;
            consultas += estado.Consultas;
            foreach (var c in estado.Consultados) Consultar(consultados, c);

            foreach (var item in estado.Itens)
            {
                string chave = Chave(item);

                if (item.Tipo != ItemDeEstado.TipoComando
                    && item.Situacao.StartsWith("APAGAD", StringComparison.OrdinalIgnoreCase))
                {
                    string prefixo = item.Alvo.TrimEnd('\\', '/') + "\\";
                    foreach (var k in itens.Keys.Where(k => k.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        var dentro = itens[k];
                        if (dentro.Situacao.StartsWith("apagad", StringComparison.OrdinalIgnoreCase)) continue;
                        Por(k, dentro with { Situacao = "apagado junto com a pasta", Detalhe = null, Turno = item.Turno });
                    }

                    if (itens.TryGetValue(chave, out var antes) && antes.Situacao.StartsWith("APAGAD", StringComparison.OrdinalIgnoreCase))
                    {
                        Por(chave, item with { Vezes = antes.Vezes + item.Vezes });
                        continue;
                    }
                }
                else if (item.Tipo == ItemDeEstado.TipoComando && itens.TryGetValue(chave, out var anterior))
                {
                    Por(chave, item with { Vezes = anterior.Vezes + item.Vezes });
                    continue;
                }

                Por(chave, item);
            }
        }

        return new EstadoDoTrecho(ordem.Select(k => itens[k]).ToList(), consultados, consultas);

        void Por(string chave, ItemDeEstado item)
        {
            if (itens.ContainsKey(chave)) ordem.Remove(chave);
            itens[chave] = item;
            ordem.Add(chave);
        }
    }

    private static string Chave(ItemDeEstado item) =>
        item.Tipo == ItemDeEstado.TipoComando ? "cmd:" + item.Alvo : item.Alvo.TrimEnd('\\', '/');

    private static void Consultar(List<string> consultados, string caminho)
    {
        consultados.RemoveAll(c => string.Equals(c, caminho, StringComparison.OrdinalIgnoreCase));
        consultados.Add(caminho);
    }

    private static string? Primeira(string? resultado)
    {
        string? linha = ArtifactExtractor.PrimeiraLinhaDoErro(resultado);
        return linha == null ? null : Regex.Replace(linha, @"^ERRO(\s*\([^)]*\))?\s*:\s*", "");
    }

    /// <summary>Chamada e resultado pareados por id, na ordem em que os resultados chegaram.</summary>
    private static IEnumerable<(string Nome, string Args, string Resultado)> Pares(IReadOnlyList<ChatMessage> mensagens)
    {
        var chamadas = new Dictionary<string, (string, string)>(StringComparer.Ordinal);

        foreach (var msg in mensagens)
        {
            if (msg is AssistantChatMessage a && a.ToolCalls is { Count: > 0 })
            {
                foreach (var c in a.ToolCalls)
                    if (c?.Id != null) chamadas[c.Id] = (c.FunctionName ?? "", c.FunctionArguments?.ToString() ?? "");
            }
            else if (msg is ToolChatMessage t && t.ToolCallId != null && chamadas.Remove(t.ToolCallId, out var ch))
            {
                yield return (ch.Item1, ch.Item2, Turn.TextOf(t));
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Texto
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// O bloco "Estado ao fim de …". Arquivos agrupados pela pasta em comum — o caminho completo
    /// uma vez no título, o nome em cada linha —, comandos depois, e a linha Consultados no fim.
    /// </summary>
    public string Render(string fimDe)
    {
        if (EstaVazio) return "";

        var texto = new StringBuilder();
        var arquivos = Itens.Where(i => i.Tipo != ItemDeEstado.TipoComando).ToList();
        var comandos = Itens.Where(i => i.Tipo == ItemDeEstado.TipoComando).ToList();

        if (Itens.Count > 0)
        {
            string? comum = PastaComum(arquivos);
            texto.Append("Estado ao fim ").Append(fimDe);
            if (comum != null) texto.Append(" (pasta ").Append(comum).Append(')');
            texto.Append(":\n");

            int mostrados = 0;
            foreach (var item in arquivos.Concat(comandos))
            {
                if (mostrados == TetoDeItens)
                {
                    texto.Append("- e mais ").Append(Itens.Count - mostrados).Append(" item(ns)\n");
                    break;
                }

                texto.Append("- ").Append(Linha(item, comum)).Append('\n');
                mostrados++;
            }
        }

        if (Consultados.Count > 0 || Consultas > 0)
        {
            var nomes = Consultados.TakeLast(TetoDeConsultados).Select(c => NomeCurto(c)).ToList();
            texto.Append("Consultados: ");
            texto.Append(nomes.Count > 0 ? string.Join(", ", nomes) : "—");
            texto.Append(" (").Append(Consultas).Append(Consultas == 1 ? " consulta)" : " consultas)").Append('\n');
        }

        return texto.ToString();
    }

    private static string Linha(ItemDeEstado item, string? comum)
    {
        if (item.Tipo == ItemDeEstado.TipoComando)
        {
            string cmd = item.Alvo.Length > 160 ? item.Alvo[..160] + "…" : item.Alvo;
            string vezes = item.Vezes > 1 ? $" ({item.Vezes}×)" : "";
            string desfecho = item.Situacao == "ok"
                ? "última: ok — efeitos em disco não rastreados"
                : item.Situacao == "FALHOU"
                    ? "última: FALHOU" + (item.Detalhe != null ? " — " + item.Detalhe : "")
                    : item.Situacao + (item.Detalhe != null ? " — " + item.Detalhe : "");
            return $"comando `{cmd}`{vezes}: {desfecho} (turno {item.Turno + 1})";
        }

        string nome = comum != null && item.Alvo.StartsWith(comum + "\\", StringComparison.OrdinalIgnoreCase)
            ? item.Alvo[(comum.Length + 1)..]
            : string.Equals(item.Alvo, comum, StringComparison.OrdinalIgnoreCase) ? "a própria pasta" : item.Alvo;

        string situacao = item.Vezes > 1 ? $"{item.Situacao} ({item.Vezes}×)" : item.Situacao;
        string linha = $"{nome}: {situacao}";
        if (!string.IsNullOrEmpty(item.Detalhe)) linha += $" ({item.Detalhe})";
        return linha + $" (turno {item.Turno + 1})";
    }

    private static string NomeCurto(string caminho)
    {
        string nome = Path.GetFileName(caminho.TrimEnd('\\', '/'));
        return nome.Length == 0 ? caminho : nome;
    }

    /// <summary>
    /// A pasta que contém todos os arquivos e pastas do estado, quando há ao menos dois. Uma
    /// pasta do estado conta como ela mesma: a pasta apagada vira o título, e o que estava nela,
    /// o nome curto.
    /// </summary>
    private static string? PastaComum(IReadOnlyList<ItemDeEstado> itens)
    {
        if (itens.Count < 2) return null;

        string comum = itens
            .Select(i => i.Tipo == ItemDeEstado.TipoPasta ? i.Alvo : Path.GetDirectoryName(i.Alvo) ?? "")
            .Aggregate((a, b) =>
            {
                while (a.Length > 0 && !(b.Equals(a, StringComparison.OrdinalIgnoreCase)
                                         || b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase)))
                    a = Path.GetDirectoryName(a) ?? "";
                return a;
            });

        return string.IsNullOrEmpty(comum) || Path.GetPathRoot(comum) == comum ? null : comum;
    }
}