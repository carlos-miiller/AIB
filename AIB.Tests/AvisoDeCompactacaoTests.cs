using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Agent;
using AIB.Services.Ai;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests;

/// <summary>
/// O aviso de que a compactação automática está perto. Pedido do usuário, 27/10/2026: a
/// compactação (20 turnos ou 135 mil tokens soltos) chegava sem aviso, e quem quer escolher a
/// hora precisa saber antes para usar o /compact.
/// </summary>
[Collection("ContextoGlobal")]
public class AvisoDeCompactacaoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AIB_Aviso_" + Guid.NewGuid().ToString("N"));

    public AvisoDeCompactacaoTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Mudo : IChatProvider
    {
        public string Name => "Fake";
        public string Model => "fake";

        // O aviso não chama o modelo: nada aqui pode ser usado.
        public IAsyncEnumerable<StreamChunk> StreamAsync(
            IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, ChatRequestOptions options,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<ChatCompletionResult> CompleteAsync(
            IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, ChatRequestOptions options,
            CancellationToken ct) => throw new NotSupportedException();

        public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Fabrica : IChatProviderFactory
    {
        public IChatProvider GetProvider(UserAppSettings settings) => new Mudo();
    }

    private ConversationService Conversa(int turnosSoltos, int tokensSoltos)
    {
        var settings = new SettingsService(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".dat"));
        settings.SaveSettings(new UserAppSettings
        {
            AiProvider = "Ollama",
            ModelName = "modelo-de-teste",
            SendSystemPrompt = false,
            ActiveCharacter = "",
            TurnosSoltos = turnosSoltos,
            TokensSoltos = tokensSoltos
        }.Sanear());

        var registro = new ToolRegistry();
        var contador = new TokenCounter();
        var fabrica = new Fabrica();
        return new ConversationService(
            settings, registro, new AgentLoop(registro, fabrica, settings, contador), contador, fabrica,
            memoryRootOverride: Path.Combine(_dir, "memory"));
    }

    [Fact]
    public void FaltandoTresTurnos_Avisa_UmaVezSo()
    {
        var conversa = Conversa(turnosSoltos: 20, tokensSoltos: 135_000);

        for (int i = 0; i < 16; i++) conversa.AppendRecoveredContext($"pedido {i}", "resposta curta");
        conversa.AvisoDeCompactacao(1).Should().BeNull("faltam quatro turnos");

        conversa.AppendRecoveredContext("pedido 16", "resposta curta");
        conversa.AvisoDeCompactacao(1).Should().Contain("em 3 turno(s)").And.Contain("/compact");

        conversa.AppendRecoveredContext("pedido 17", "resposta curta");
        conversa.AvisoDeCompactacao(1).Should().BeNull("um aviso por aproximação, e não um por turno");
    }

    [Fact]
    public void PertoDoGatilhoDeTokens_Avisa()
    {
        // Gatilho de 8 mil (o mínimo): turnos de ~500 tokens cruzam os 85% bem antes dos 200
        // turnos, e o aviso diz quanto há e quanto é o gatilho.
        var conversa = Conversa(turnosSoltos: 200, tokensSoltos: 8_000);
        string? aviso = null;

        for (int i = 0; i < 40 && aviso == null; i++)
        {
            conversa.AppendRecoveredContext($"pedido {i}", new string('a', 2000) + " palavras para gastar tokens.");
            aviso = conversa.AvisoDeCompactacao(1);
        }

        aviso.Should().Contain("de 8 mil tokens soltos").And.Contain("/compact");
    }

    [Fact]
    public void ConversaLeve_NaoAvisa()
    {
        var conversa = Conversa(turnosSoltos: 20, tokensSoltos: 135_000);
        conversa.AppendRecoveredContext("pedido", "resposta");

        conversa.AvisoDeCompactacao(1).Should().BeNull();
    }
}
