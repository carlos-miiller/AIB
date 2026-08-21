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

            string[] perguntas =
            {
                "Explique em um parágrafo o que é memória virtual no Windows.",
                "E o que é um arquivo de paginação? Um parágrafo.",
                "Qual a diferença entre RAM e VRAM? Um parágrafo.",
                "Explique o que faz o agendador de tarefas do Windows. Um parágrafo.",
                "O que é uma DLL? Um parágrafo.",
                "Explique o registro do Windows em um parágrafo.",
                "O que é um serviço do Windows? Um parágrafo.",
                "Explique o que é o UAC em um parágrafo.",
                "O que faz o PowerShell diferente do CMD? Um parágrafo.",
                "Explique o que é NTFS em um parágrafo."
            };

            for (int i = 0; i < perguntas.Length && conversa.Chapters.Count == 0; i++)
            {
                var relogio = System.Diagnostics.Stopwatch.StartNew();
                await foreach (var _ in conversa.StreamResponseAsync(perguntas[i])) { }
                relogio.Stop();

                _saida.WriteLine(
                    $"turno {i + 1,2}: {conversa.CurrentTokenCount,6} tokens de {orcamento} " +
                    $"| {relogio.Elapsed.TotalSeconds,6:F1}s | capítulos: {conversa.Chapters.Count}");
            }

            conversa.Chapters.Should().NotBeEmpty(
                "o gatilho deveria disparar dentro das perguntas previstas — se não disparou, " +
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

        [Fact]
        public async Task CadeiaComFerramenta_PreservaOCaminhoLiteralNoCapitulo()
        {
            if (Desligado()) return;

            // Arquivo real para o agente ler: read_file é nível 1 e não passa pelo portão,
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

        private static string Texto(ChatMessage mensagem) =>
            mensagem.Content == null
                ? ""
                : string.Concat(mensagem.Content.Where(p => p?.Text != null).Select(p => p.Text));
    }
}
