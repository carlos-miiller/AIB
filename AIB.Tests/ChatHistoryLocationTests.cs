using System;
using System.IO;
using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Onde o histórico de conversas é gravado.
    /// <para>
    /// A regra do projeto é que a pasta do usuário — <c>~/.AIB</c> — é a autoridade, e a do
    /// programa é failsafe de leitura. O histórico era o único arquivo do usuário que ainda
    /// morava em <c>%APPDATA%\AIB</c>, sobra da migração antiga. Um caminho errado aqui não
    /// quebra nada visível: o app abre, grava e lê normalmente, só que no lugar errado — por
    /// isso a checagem é de ensaio e não de olho.
    /// </para>
    /// </summary>
    public class ChatHistoryLocationTests
    {
        [Fact]
        public void SemSobrescrita_OHistoricoMoraNaPastaDoUsuario()
        {
            ChatHistoryService.ResolverDiretorio(null).Should().Be(DirectoryService.DataDir);
        }

        [Fact]
        public void SemSobrescrita_OHistoricoNaoMoraMaisNoAppData()
        {
            string antigo = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIB");

            ChatHistoryService.ResolverDiretorio(null).Should().NotStartWith(antigo);
        }

        [Fact]
        public void ASuite_NaoEscreveNoHistoricoRealDoUsuario()
        {
            // Fechar uma ChatWindow de ensaio arquiva a conversa. Sem o desvio do
            // TestAuditRedirect, a suíte deixava um chat_history.json de mentira no ~/.AIB.
            ChatHistoryService.HistoryDirectoryOverride.Should().NotBeNullOrWhiteSpace(
                "a suíte precisa gravar histórico em pasta temporária");

            ChatHistoryService.ResolverDiretorio(ChatHistoryService.HistoryDirectoryOverride)
                .Should().NotBe(DirectoryService.DataDir);
        }
    }
}
