using AIB.Services.Mail;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O botão "Abrir no &lt;provedor&gt;".
    /// <para>
    /// O defeito: toda linha da caixa nascia com a URL vazia, e o botão "Abrir no Gmail" não abria
    /// nada — em silêncio, porque endereço vazio faz o navegador nem ser chamado. O rótulo tinha
    /// regra; o endereço não tinha. Agora os dois saem da mesma.
    /// </para>
    /// </summary>
    public class LinkDoEmailTests
    {
        private const string Thread = "1789000000000000777";

        [Fact]
        public void Gmail_AbreAConversa_NaContaCerta()
        {
            string url = LinkDoEmail.Para("eu@gmail.com", Thread);

            url.Should().StartWith("https://mail.google.com/mail/u/?authuser=eu%40gmail.com",
                "sem authuser o navegador abriria a primeira conta logada, que pode ser outra caixa");
            url.Should().EndWith("#all/" + ulong.Parse(Thread).ToString("x"),
                "a interface web identifica a conversa pelo X-GM-THRID em hexadecimal");
        }

        [Fact]
        public void GmailSemThread_AbreACaixaDaConta()
        {
            LinkDoEmail.Para("eu@gmail.com", "").Should().EndWith("#inbox");
        }

        [Fact]
        public void WorkspaceComDominioProprio_EhGmailPelaThread()
        {
            // voce@empresa.com.br não diz "gmail" no endereço, mas só o Gmail entrega X-GM-THRID.
            LinkDoEmail.Rotulo("voce@empresa.com.br", Thread).Should().Be("Abrir no Gmail");
            LinkDoEmail.Para("voce@empresa.com.br", Thread).Should().Contain("mail.google.com");
        }

        [Fact]
        public void Outlook_VaiParaACaixaDoOutlook()
        {
            LinkDoEmail.Rotulo("eu@outlook.com", "").Should().Be("Abrir no Outlook");
            LinkDoEmail.Para("eu@outlook.com", "").Should().Contain("outlook.live.com");
        }

        [Fact]
        public void ProvedorDesconhecido_NaoInventaEndereco()
        {
            // Vazio, e o botão some: um link chutado levaria a um lugar que não é a caixa.
            LinkDoEmail.Rotulo("eu@provedor.com.br", "").Should().Be("Abrir no cliente");
            LinkDoEmail.Para("eu@provedor.com.br", "").Should().BeEmpty();
        }
    }
}
