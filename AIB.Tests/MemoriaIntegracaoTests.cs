using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Agent;
using AIB.Services.Ai;
using AIB.Services.Memory;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;
using Xunit.Abstractions;

namespace AIB.Tests
{
    /// <summary>
    /// Ensaio de ponta a ponta contra o Ollama REAL. Fica desligado por padrão — a suíte
    /// normal não pode depender de um modelo carregado nem levar minutos.
    /// <para>
    /// Ligar com:
    /// <code>
    /// $env:AIB_INTEGRACAO = "1"
    /// dotnet test AIB.Tests --filter Categoria=Integracao -l "console;verbosity=detailed"
    /// </code>
    /// Modelo e URL saem de AIB_MODELO e AIB_URL, com padrão qwen3.5:9b em localhost:11434.
    /// </para>
    /// <para>
    /// Escreve num diretório temporário próprio: não toca ~/.AIB do usuário.
    /// </para>
    /// </summary>
    [Trait("Categoria", "Integracao")]
    public class MemoriaIntegracaoTests : IDisposable
    {
        private readonly ITestOutputHelper _saida;
        private readonly string _dir;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

        private static bool Ligado =>
            Environment.GetEnvironmentVariable("AIB_INTEGRACAO") == "1";

        private static string Modelo =>
            Environment.GetEnvironmentVariable("AIB_MODELO") ?? "qwen3.5:9b";

        private static string Url =>
            Environment.GetEnvironmentVariable("AIB_URL") ?? "http://localhost:11434";

        /// <summary>
        /// Teto de turnos dos ensaios longos. Existe só para o ensaio não rodar para sempre se
        /// o gatilho parar de disparar — não é uma previsão de quantos turnos serão precisos.
        /// A primeira versão deste arquivo chutava dez perguntas fixas e falhou com 2.350 tokens
        /// de 5.088: prever o gatilho em vez de esperá-lo é o mesmo erro que já deixou os testes
        /// de poda passando sem nunca podar nada.
        /// </summary>
        private const int TetoDeTurnos = 30;

        /// <summary>
        /// Uma pergunta com material colado junto, como o usuário de verdade faz.
        /// <para>
        /// O gatilho é por TOKEN, e token de pergunta custa muito menos que token de resposta:
        /// medido aqui, prefill roda a ~34 tok/s e geração a ~7,7 tok/s. Encher a conversa pelo
        /// lado do usuário cruza o gatilho em poucos turnos sem transformar o ensaio numa espera
        /// de vinte minutos — e ainda exercita o caso mais realista, o do log colado no chat.
        /// </para>
        /// <para>
        /// Os temas variam de propósito: repetir a mesma pergunta faria o modelo repetir a
        /// resposta, e resumos idênticos esconderiam um resumidor que não está lendo nada.
        /// </para>
        /// </summary>
        private static string Pergunta(int i)
        {
            string[] temas =
            {
                "memória virtual", "o arquivo de paginação", "a diferença entre RAM e VRAM",
                "o agendador de tarefas", "as DLLs", "o registro do Windows",
                "os serviços do Windows", "o UAC", "a diferença entre PowerShell e CMD",
                "o sistema de arquivos NTFS", "as variáveis de ambiente",
                "processos e threads", "o firewall", "a área de transferência",
                "as permissões NTFS", "os links simbólicos", "os pontos de restauração",
                "o gerenciador de tarefas", "o modo de segurança", "o Windows Update"
            };

            var material = new System.Text.StringBuilder();
            for (int linha = 0; linha < 40; linha++)
            {
                material.Append($"2026-08-24 10:{linha:D2}:00 INFO  servico-{i}-{linha} ")
                        .Append("processou o lote de trabalho e registrou o resultado no diario ")
                        .Append($"do sistema, com duracao de {linha * 7 + 13} ms e nenhum aviso.\n");
            }

            return $"Segue um trecho de log da minha máquina:\n\n{material}\n" +
                   $"Com base nisso, explique {temas[i % temas.Length]} no Windows em um parágrafo.";
        }

        public MemoriaIntegracaoTests(ITestOutputHelper saida)
        {
            _saida = saida;
            _dir = Path.Combine(Path.GetTempPath(), "AIB_Integracao_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            _http.Dispose();
            try { Directory.Delete(_dir, true); } catch { }
        }

        private (ConversationService Conversa, SettingsService Settings) Montar()
        {
            var settings = new SettingsService(Path.Combine(_dir, "profile.dat"));
            settings.SaveSettings(new UserAppSettings
            {
                AiProvider = "Ollama",
                ModelName = Modelo,
                ApiUrl = Url,
                EnableIntelligentTools = true,
                SendSystemPrompt = true,
                MessageCount = 0,          // nível 1: o orçamento mais apertado, o que estressa a memória
                ActiveCharacter = "",      // sem alma: o teste não depende do ~/.AIB do usuário
                VerboseConsoleLogging = false
            });

            var registry = new ToolRegistry();
            var counter = new TokenCounter();
            var factory = new ChatProviderFactory(_http, new RegexToolCallHealer());
            var loop = new AgentLoop(registry, factory, settings, counter);

            var conversa = new ConversationService(
                settings, registry, loop, counter, factory,
                memoryRootOverride: Path.Combine(_dir, "memory"));

            return (conversa, settings);
        }

        [Fact]
        public async Task ConversaLonga_FechaCapituloComResumoDeVerdade()
        {
            if (Desligado()) return;

            var (conversa, _) = Montar();
            int orcamento = LevelService.GetMaxTokensForLevel(1);

            for (int i = 0; i < TetoDeTurnos && conversa.Chapters.Count == 0; i++)
            {
                var relogio = System.Diagnostics.Stopwatch.StartNew();
                await foreach (var _ in conversa.StreamResponseAsync(Pergunta(i))) { }
                relogio.Stop();

                Progresso(
                    $"turno {i + 1,2}: {conversa.CurrentTokenCount,6} tokens de {orcamento} " +
                    $"| {relogio.Elapsed.TotalSeconds,6:F1}s | capítulos: {conversa.Chapters.Count}");
            }

            conversa.Chapters.Should().NotBeEmpty(
                $"o gatilho deveria disparar dentro de {TetoDeTurnos} turnos — se não disparou, " +
                "o orçamento do nível ou o tamanho das respostas mudou");

            var capitulo = conversa.Chapters[0];

            _saida.WriteLine("");
            _saida.WriteLine($"── capítulo {capitulo.Index} (turnos {capitulo.FirstTurn}–{capitulo.LastTurn}) ──");
            _saida.WriteLine(capitulo.Summary);
            foreach (var artefato in capitulo.Artifacts) _saida.WriteLine(artefato.Render());

            capitulo.Summary.Should().NotContain("indisponível", "o modelo respondeu, o resumo tem de ser real");
            capitulo.Summary.Length.Should().BeGreaterThan(40);

            // O bloco de memória entrou no prompt, e a primeira mensagem seguiu intacta.
            var historico = conversa.SnapshotHistory();
            historico[0].Should().BeOfType<SystemChatMessage>();
            historico[1].Should().BeOfType<SystemChatMessage>();
            Texto(historico[1]).Should().Contain("Memória da conversa");

            _saida.WriteLine("");
            _saida.WriteLine("── bloco de memória no prompt ──");
            _saida.WriteLine(Texto(historico[1]));
        }

        /// <summary>
        /// O ensaio LENTO: conversa até os capítulos soltos fecharem um ato — pela cota, que no
        /// nível 1 do Ollama estoura com poucos capítulos, ou pelo teto de doze. Contra um modelo
        /// local na CPU isso passa de dez minutos — é o preço de exercitar a hierarquia inteira com o
        /// resumidor de verdade, e não com um dublê que devolve a mesma frase.
        /// </summary>
        [Fact]
        public async Task ConversaMuitoLonga_PromoveCapitulosAAtoEDestilaFatos()
        {
            if (Desligado()) return;

            var (conversa, _) = Montar();
            int orcamento = LevelService.GetMaxTokensForLevel(1);
            var relogioTotal = System.Diagnostics.Stopwatch.StartNew();

            // Vários capítulos, e cada capítulo custa uma conversa inteira: o teto é o do ensaio
            // de capítulo multiplicado por quatro. Basta porque, no nível 1, a cota de capítulos
            // tem poucas centenas de tokens e promove bem antes do teto de doze.
            for (int i = 0; i < TetoDeTurnos * 4 && conversa.Acts.Count == 0; i++)
            {
                var relogio = System.Diagnostics.Stopwatch.StartNew();
                await foreach (var _ in conversa.StreamResponseAsync(Pergunta(i))) { }
                relogio.Stop();

                Progresso(
                    $"turno {i + 1,2}: {conversa.CurrentTokenCount,6}/{orcamento} " +
                    $"| {relogio.Elapsed.TotalSeconds,6:F1}s " +
                    $"| capítulos: {conversa.Chapters.Count} | atos: {conversa.Acts.Count}");
            }

            Progresso($"total: {relogioTotal.Elapsed.TotalMinutes:F1} min");

            conversa.Acts.Should().NotBeEmpty(
                $"os capítulos soltos deveriam ter estourado a cota e fechado um ato dentro de {TetoDeTurnos * 4} turnos");

            var ato = conversa.Acts[0];
            _saida.WriteLine("");
            _saida.WriteLine($"── ato {ato.Index} (capítulos {ato.FirstChapter}–{ato.LastChapter}) ──");
            _saida.WriteLine(ato.Summary);
            foreach (var artefato in ato.Artifacts) _saida.WriteLine(artefato.Render());

            ato.Summary.Should().NotContain("indisponível", "o modelo respondeu, o resumo tem de ser real");

            // O ato substitui no PROMPT os capítulos que resumiu, e não em disco.
            string bloco = Texto(conversa.SnapshotHistory()[1]);
            _saida.WriteLine("");
            _saida.WriteLine("── bloco de memória no prompt ──");
            _saida.WriteLine(bloco);

            bloco.Should().Contain("### Ato 1");
            bloco.Should().NotContain("### Capítulo 1");

            File.ReadAllLines(Path.Combine(conversa.SessionMemoryDir, "chapters.jsonl"))
                .Length.Should().BeGreaterThanOrEqualTo(ato.LastChapter - ato.FirstChapter + 1,
                    "capítulo resumido continua em disco — todos os que o ato cobre, quantos forem");
        }

        [Fact]
        public async Task CadeiaComFerramenta_PreservaOCaminhoLiteralNoCapitulo()
        {
            if (Desligado()) return;

            // Arquivo real para o agente ler: read é nível 1 e não passa pelo portão,
            // então a cadeia roda sem modal.
            string alvo = Path.Combine(_dir, "alvo-do-teste.txt");
            await File.WriteAllTextAsync(alvo, "O código secreto do projeto é ABACAXI-42.");

            var (conversa, _) = Montar();

            await foreach (var _ in conversa.StreamResponseAsync($"Leia o arquivo {alvo} e me diga o que tem nele.")) { }

            var registros = new SessionMemory(
                Path.GetFileName(conversa.SessionMemoryDir),
                Path.Combine(_dir, "memory")).ReadTurns();

            _saida.WriteLine($"turnos gravados: {registros.Count}");
            foreach (var turno in registros)
                foreach (var artefato in turno.Artifacts)
                    _saida.WriteLine(artefato.Render());

            registros.Should().NotBeEmpty("o turno fechado tem de estar em raw.jsonl");

            var artefatos = registros.SelectMany(t => t.Artifacts).ToList();
            artefatos.Should().Contain(a => a.Value == alvo,
                "o caminho literal é o que importa — resumi-lo destruiria a informação");
        }

        [Fact]
        public async Task ModeloResponde_OuOEnsaioApontaOndeOlhar()
        {
            if (Desligado()) return;

            var (conversa, _) = Montar();
            var texto = new System.Text.StringBuilder();

            await foreach (var item in conversa.StreamResponseAsync("Responda apenas: pronto."))
                if (item is ChatStreamItem.Text t) texto.Append(t.Value);

            _saida.WriteLine($"resposta: \"{texto}\"");
            _saida.WriteLine($"tokens: {conversa.CurrentTokenCount}");

            texto.ToString().Trim().Should().NotBeEmpty(
                $"o modelo {Modelo} em {Url} devolveu stream vazio — verifique 'ollama ps'");
        }

        /// <summary>
        /// Sem AIB_INTEGRACAO=1 o ensaio não roda e passa em branco. Isso é uma decisão
        /// consciente: xunit 2.5 não traz Skip dinâmico, e acrescentar um pacote só para pintar
        /// de amarelo o que já é opt-in por Trait não vale. A linha impressa é o aviso.
        /// </summary>
        private bool Desligado()
        {
            if (Ligado) return false;
            _saida.WriteLine("PULADO: defina AIB_INTEGRACAO=1 para rodar contra o Ollama real.");
            return true;
        }

        /// <summary>
        /// Linha de progresso nos dois canais.
        /// <para>
        /// O <see cref="ITestOutputHelper"/> só é descarregado quando o teste TERMINA — num
        /// ensaio de vinte minutos isso significa vinte minutos de tela parada, sem como saber
        /// se está andando ou travado. O Console sai na hora.
        /// </para>
        /// </summary>
        private void Progresso(string linha)
        {
            _saida.WriteLine(linha);
            Console.WriteLine($"[ENSAIO] {linha}");
        }

        private static string Texto(ChatMessage mensagem) =>
            mensagem.Content == null
                ? ""
                : string.Concat(mensagem.Content.Where(p => p?.Text != null).Select(p => p.Text));
    }
}
