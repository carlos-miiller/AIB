using System.Windows;
using AIB.Services.Terminal;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// No modo orbe, as perguntas (confirmação do portão, senha do terminal) aparecem numa
    /// janela logo acima do orbe, e não abrindo a conversa ou no centro da tela.
    /// </summary>
    public class PerguntasPertoDoOrbeTests
    {
        private static readonly Rect Area = new(0, 0, 1920, 1040);

        [Theory]
        [InlineData(false, true, true, "modo orbe: janela perto dele")]
        [InlineData(true, false, false, "conversa aberta: o card vai nela")]
        [InlineData(false, false, false, "orbe desligado: abrir a conversa é o único lugar")]
        [InlineData(true, true, false, "as duas na tela não acontece, mas a conversa ganha")]
        public void OndeAConfirmacaoAparece(bool conversa, bool orbe, bool janela, string porque)
        {
            ChatWindow.DeveUsarJanelaDoOrbe(conversa, orbe).Should().Be(janela, porque);
        }

        [Fact]
        public void AJanela_FicaAcimaDoOrbe_ECentradaNele()
        {
            var orbe = new Rect(1800, 950, 56, 56);
            var p = PertoDoOrbe.Calcular(orbe, new Size(400, 300), Area);

            (p.Y + 300).Should().Be(orbe.Top - PertoDoOrbe.Folga, "a borda de baixo encosta na folga");
            p.X.Should().Be(1920 - 400, "centrada no orbe, mas presa dentro da tela");
        }

        [Fact]
        public void ASombra_NaoConta_NaFolga()
        {
            var orbe = new Rect(800, 950, 56, 56);
            var p = PertoDoOrbe.Calcular(orbe, new Size(400, 300), Area, sombraEmbaixo: 45);

            (p.Y + 300 - 45).Should().Be(orbe.Top - PertoDoOrbe.Folga);
            (p.X + 200).Should().Be(orbe.Left + 28);
        }

        [Fact]
        public void SemEspacoAcima_VaiParaBaixo()
        {
            var orbe = new Rect(100, 40, 56, 56);
            var p = PertoDoOrbe.Calcular(orbe, new Size(400, 300), Area);

            p.Y.Should().Be(orbe.Bottom + PertoDoOrbe.Folga);
            p.X.Should().Be(0);
        }

        [Fact]
        public void SemOrbe_NaoMexeNaJanela()
        {
            PertoDoOrbe.Ancora = null;
            PertoDoOrbe.Ativo.Should().BeFalse();
        }

        // ── O que a senha pede ──────────────────────────────────────────────

        [Theory]
        [InlineData("root@172.16.10.3's password: ", TipoDePedido.Senha, "Senha de root em 172.16.10.3", "Senha de root")]
        [InlineData("(root@172.16.10.3) Password: ", TipoDePedido.Senha, "Senha de root em 172.16.10.3", "Senha de root")]
        [InlineData("Enter passphrase for key 'C:/Users/Carlo/.ssh/id_ed25519': ", TipoDePedido.Senha, "Frase-senha da chave id_ed25519", "Frase-senha da chave")]
        [InlineData("Username for 'https://github.com': ", TipoDePedido.Texto, "Usuário para github.com", "Usuário")]
        [InlineData("Password for 'https://carlo@github.com': ", TipoDePedido.Senha, "Senha para github.com", "Senha")]
        [InlineData("Verification code: ", TipoDePedido.Senha, "O comando pede uma senha", "Senha")]
        public void OTitulo_DizOQueEDeQuem(string prompt, TipoDePedido tipo, string titulo, string rotulo)
        {
            SenhaDoTerminalDialog.OQuePede(prompt, tipo).Should().Be((titulo, rotulo));
        }

        [Fact]
        public void Fingerprint_NomeiaOServidor()
        {
            const string prompt = "The authenticity of host '172.16.10.3 (172.16.10.3)' can't be established.\n"
                                  + "Are you sure you want to continue connecting (yes/no/[fingerprint])? ";
            SenhaDoTerminalDialog.OQuePede(prompt, TipoDePedido.SimNao).Titulo
                .Should().Be("Confiar no servidor 172.16.10.3?");
        }
    }
}
