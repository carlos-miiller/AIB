using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

// WinForms entra junto com o WPF em net8.0-windows e traz um Timer homonimo.
using Timer = System.Threading.Timer;

namespace AIB.Services.Agent;

/// <summary>
/// Prova de vida no terminal enquanto o turno roda.
/// <para>
/// O problema que ele resolve é concreto e medido nesta máquina: com o 9B, um prefill frio de
/// ~3500 tokens leva mais de três minutos, e nesse intervalo NADA acontece na tela nem no
/// console. O <c>[STREAM-END]</c> só imprime quando o stream acaba, e o <c>[STREAM-DBG]</c> só
/// no primeiro chunk com conteúdo — os dois chegam depois do silêncio, não durante. Do lado de
/// fora, isso é indistinguível de travamento.
/// </para>
/// <para>
/// Não depende de <c>VerboseConsoleLogging</c> de propósito. O log detalhado é para diagnosticar
/// defeito; isto é o sinal de que o programa está vivo, e um sinal de vida que precisa ser
/// ligado antes não serve para a primeira vez em que a dúvida aparece.
/// </para>
/// </summary>
public sealed class PulsoDoTurno : IDisposable
{
    /// <summary>Fase do turno. É o que muda o texto da linha de pulso.</summary>
    private enum Fase
    {
        /// <summary>Requisição enviada, nenhum token de volta. É aqui que mora o silêncio.</summary>
        Prefill,

        /// <summary>Chegando raciocínio pelo canal separado.</summary>
        Pensando,

        /// <summary>Chegando texto da resposta.</summary>
        Escrevendo,

        /// <summary>Ferramenta em execução; o modelo está parado esperando.</summary>
        Ferramenta
    }

    /// <summary>
    /// Entre uma linha e a seguinte. Cinco segundos numa espera de três minutos dá 36 linhas —
    /// bastante para acompanhar, longe de virar rolagem descontrolada.
    /// </summary>
    public static readonly TimeSpan IntervaloPadrao = TimeSpan.FromSeconds(5);

    private readonly Action<string> _escrever;
    private readonly Stopwatch _relogio = Stopwatch.StartNew();
    private readonly Timer? _timer;
    private readonly int _iteracao;

    /// <summary>
    /// Modelo rodando nesta máquina. Prefill, reuso de prefixo e num_ctx só existem aqui: no
    /// OpenRouter o modelo roda do outro lado, a espera é rede e fila, e falar em prefill
    /// apontaria para uma causa que não é a dele.
    /// </summary>
    private readonly bool _local;

    // Escritos pela thread do turno, lidos pela do timer.
    private int _fase = (int)Fase.Prefill;
    private int _caracteresDeTexto;
    private int _caracteresDeRaciocinio;
    private long _milissegundosDoPrimeiroToken = -1;
    private string _ferramenta = "";
    private long _milissegundosDaFerramenta;
    private int _ferramentasExecutadas;
    private int _encerrado;

    /// <summary>
    /// Quanto cada ferramenta passou parada no cartão de confirmação, esperando o usuário. Por NOME e não um
    /// total, porque as ferramentas de um turno rodam em paralelo.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _esperaHumana = new();

    private long _esperaHumanaTotal;

    /// <param name="iteracao">Volta do laço do agente. Um turno com ferramenta tem várias.</param>
    /// <param name="modelo">Modelo efetivo do provider, não o das configurações.</param>
    /// <param name="tokensDoPrompt">Tamanho do prompt que está indo.</param>
    /// <param name="reusoPrevisto">
    /// Quanto do prompt o cache de prefixo deve reaproveitar. É o número que EXPLICA a espera:
    /// reuso alto significa segundos, reuso zero significa minutos. Sem ele, uma espera longa e
    /// uma curta são visualmente idênticas até acabarem.
    /// </param>
    /// <param name="escrever">Saída. Injetável para o ensaio não depender do console.</param>
    /// <param name="local">Modelo no Ollama. Fora dele o pulso não fala em prefill, reuso nem ctx.</param>
    public PulsoDoTurno(
        int iteracao,
        string modelo,
        int tokensDoPrompt,
        int reusoPrevisto,
        int numCtx,
        TimeSpan? intervalo = null,
        Action<string>? escrever = null,
        bool local = true)
    {
        _iteracao = iteracao;
        _escrever = escrever ?? Console.WriteLine;
        _local = local;

        int novos = Math.Max(0, tokensDoPrompt - reusoPrevisto);

        Linha(local
            ? $"> prompt {tokensDoPrompt} tok (reuso previsto {reusoPrevisto}, novos {novos}) · {modelo} · ctx {numCtx}"
            : $"> prompt {tokensDoPrompt} tok · {modelo}");

        var passo = intervalo ?? IntervaloPadrao;
        _timer = new Timer(_ => Bater(), null, passo, passo);
    }

    /// <summary>
    /// Força uma batida sem esperar o relógio. Diagnóstico e ensaio: sem isto, todo ensaio de
    /// conteúdo de linha dependeria de dormir por alguns segundos.
    /// </summary>
    public void BaterAgora() => Bater();

    /// <summary>
    /// O primeiro token chegou. Marca o fim do prefill, que é a métrica que interessa: é a
    /// espera que não tem nenhum outro sinal.
    /// </summary>
    public void PrimeiroToken()
    {
        if (Interlocked.CompareExchange(ref _milissegundosDoPrimeiroToken, _relogio.ElapsedMilliseconds, -1) != -1)
            return;

        Linha($"* primeiro token em {Segundos(_relogio.ElapsedMilliseconds)}");
    }

    public void Raciocinou(int caracteres)
    {
        PrimeiroToken();
        Interlocked.Add(ref _caracteresDeRaciocinio, caracteres);
        Interlocked.Exchange(ref _fase, (int)Fase.Pensando);
    }

    public void Escreveu(int caracteres)
    {
        PrimeiroToken();
        Interlocked.Add(ref _caracteresDeTexto, caracteres);
        Interlocked.Exchange(ref _fase, (int)Fase.Escrevendo);
    }

    public void FerramentaComecou(string nome)
    {
        Interlocked.Exchange(ref _ferramenta, nome ?? "");
        Interlocked.Exchange(ref _milissegundosDaFerramenta, _relogio.ElapsedMilliseconds);
        Interlocked.Exchange(ref _fase, (int)Fase.Ferramenta);
        Linha($"~ ferramenta {nome} — executando");
    }

    /// <summary>
    /// Anota que a ferramenta ficou parada esperando o usuário decidir no cartão de confirmação.
    /// <para>
    /// Existe porque o log dizia "ferramenta shell — ok em 7299,6s" para um
    /// <c>Get-Content</c> trivial: as duas horas eram do modal aberto, não da execução. Misturar
    /// as duas coisas faz o registro afirmar que a máquina é lenta quando ela estava parada
    /// esperando gente.
    /// </para>
    /// </summary>
    public void EsperaHumana(string ferramenta, long milissegundos)
    {
        if (milissegundos <= 0) return;

        _esperaHumana.AddOrUpdate(ferramenta ?? "", milissegundos, (_, antes) => antes + milissegundos);
        Interlocked.Add(ref _esperaHumanaTotal, milissegundos);
    }

    public void FerramentaTerminou(string nome, bool falhou)
    {
        long duracao = _relogio.ElapsedMilliseconds - Interlocked.Read(ref _milissegundosDaFerramenta);
        Interlocked.Increment(ref _ferramentasExecutadas);
        Interlocked.Exchange(ref _ferramenta, "");
        Interlocked.Exchange(ref _fase, (int)Fase.Prefill);

        _esperaHumana.TryRemove(nome ?? "", out long esperou);

        string sufixo = esperou > 0
            ? $" em {Segundos(Math.Max(0, duracao - esperou))} (+ {Duracao(esperou)} esperando você)"
            : $" em {Segundos(duracao)}";

        Linha($"~ ferramenta {nome} — {(falhou ? "FALHOU" : "ok")}{sufixo}");

        // Depois da ferramenta vem outra ida ao modelo, com outro prefill. Voltar a Prefill faz
        // a próxima espera ser anunciada como espera, e não como "escrevendo" congelado.
    }

    // Relato do provedor sobre a volta. Escritos pela thread do turno antes do Fim.
    private int? _cacheRelatado;
    private int? _entradaRelatada;
    private string? _provedor;

    /// <summary>
    /// O que o provedor relatou da volta: entrada servida do cache, entrada total e quem atendeu.
    /// Só aparece fora do Ollama — lá o cache não é relatado, e o reuso previsto já está na
    /// primeira linha. Serve para ler no terminal, volta a volta, se o prefixo repetido está
    /// saindo barato: sem isto, sete voltas de 3.231 a 5.408 tokens não diziam quanto foi cache.
    /// </summary>
    public void Relato(int? tokensDoCache, int? tokensDeEntrada, string? provedor)
    {
        _cacheRelatado = tokensDoCache;
        _entradaRelatada = tokensDeEntrada;
        _provedor = string.IsNullOrWhiteSpace(provedor) ? null : provedor;
    }

    /// <summary>Fecha o pulso com o resumo. Idempotente: o Dispose chama de novo sem repetir.</summary>
    public void Fim(string desfecho, int? tokensGerados = null)
    {
        if (Interlocked.Exchange(ref _encerrado, 1) == 1) return;

        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _relogio.Stop();

        long total = _relogio.ElapsedMilliseconds;
        long espera = Interlocked.Read(ref _esperaHumanaTotal);
        long prefill = Interlocked.Read(ref _milissegundosDoPrimeiroToken);

        // O tempo de gente sai do total antes de qualquer taxa ser calculada: com ele dentro,
        // um turno de 7.648s dividia 463 caracteres e anunciava "0,1 car/s", como se o modelo
        // estivesse agonizando.
        total = Math.Max(1, total - espera);
        int caracteres = _caracteresDeTexto + _caracteresDeRaciocinio;

        var resumo = $"< {desfecho} em {Segundos(total)}";

        if (prefill >= 0)
        {
            resumo += _local ? $" · prefill {Segundos(prefill)}" : $" · 1º token {Segundos(prefill)}";

            long geracao = Math.Max(1, total - prefill);
            if (tokensGerados is > 0)
                resumo += $" · {tokensGerados} tok a {(tokensGerados.Value * 1000.0 / geracao):0.0} tok/s";
            else if (caracteres > 0)
                resumo += $" · {caracteres} car a {(caracteres * 1000.0 / geracao):0.0} car/s";
        }
        else
        {
            resumo += " · nenhum token recebido";
        }

        if (_ferramentasExecutadas > 0) resumo += $" · {_ferramentasExecutadas} ferramenta(s)";
        if (espera > 0) resumo += $" · {Duracao(espera)} esperando você";

        if (!_local)
        {
            if (_cacheRelatado is int cache)
                resumo += _entradaRelatada is int entrada
                    ? $" · cache {cache:N0}/{entrada:N0}"
                    : $" · cache {cache:N0}";
            if (_provedor != null) resumo += $" · via {_provedor}";
        }

        Linha(resumo);
    }

    public void Dispose()
    {
        // Fecha mesmo quando o turno morre por exceção ou cancelamento: um pulso que só para no
        // caminho feliz continuaria batendo para um turno que já acabou.
        Fim("interrompido");
        _timer?.Dispose();
    }

    private void Bater()
    {
        if (Volatile.Read(ref _encerrado) == 1) return;

        long agora = _relogio.ElapsedMilliseconds;
        var fase = (Fase)Volatile.Read(ref _fase);

        string detalhe = fase switch
        {
            Fase.Prefill => _local
                ? "aguardando o primeiro token (prefill)"
                : "aguardando o primeiro token",

            Fase.Pensando =>
                $"pensando · {Volatile.Read(ref _caracteresDeRaciocinio)} car",

            Fase.Escrevendo =>
                $"escrevendo · {Volatile.Read(ref _caracteresDeTexto)} car",

            Fase.Ferramenta =>
                $"ferramenta {Volatile.Read(ref _ferramenta)} há "
                + Segundos(agora - Interlocked.Read(ref _milissegundosDaFerramenta)),

            _ => "trabalhando"
        };

        Linha($". {Segundos(agora)} — {detalhe}");
    }

    private void Linha(string texto) => _escrever($"[TURNO {_iteracao}] {texto}");

    private static string Segundos(long milissegundos) => $"{milissegundos / 1000.0:0.0}s";

    /// <summary>
    /// Duração legível para esperas longas. "7299,6s" é um número que ninguém converte de
    /// cabeça; "2h01min" se lê de relance, e a espera humana é justamente a que fica grande.
    /// </summary>
    public static string Duracao(long milissegundos)
    {
        double s = milissegundos / 1000.0;

        if (s < 90) return $"{s:0.0}s";
        if (s < 3600) return $"{(int)(s / 60)}min{(int)(s % 60):00}s";

        return $"{(int)(s / 3600)}h{((int)s % 3600) / 60:00}min";
    }
}
