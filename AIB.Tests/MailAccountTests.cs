using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIB.Services;
using AIB.Services.Mail;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A dedução de servidor — §9 passo 3 de tela-configuracoes.
    /// </summary>
    public class ImapHostGuesserTests
    {
        [Theory]
        [InlineData("alguem@gmail.com", "imap.gmail.com")]
        [InlineData("ALGUEM@GMAIL.COM", "imap.gmail.com")]
        [InlineData("alguem@outlook.com", "outlook.office365.com")]
        [InlineData("alguem@icloud.com", "imap.mail.me.com")]
        public void DominioConhecido_TemUmCandidatoSo(string endereco, string host)
        {
            var lista = ImapHostGuesser.Candidatos(endereco);

            lista.Should().HaveCount(1, "quando o host e sabido nao se adivinha");
            lista[0].Host.Should().Be(host);
            lista[0].Port.Should().Be(993);
            lista[0].UseSsl.Should().BeTrue();
        }

        [Fact]
        public void DominioProprio_TentaImapDepoisMailDepoisGMAIL()
        {
            // A terceira tentativa e a que importa e a que a spec nao tinha. Uma caixa de
            // Google Workspace em dominio proprio nao responde em imap.empresa.com.br nem em
            // mail.empresa.com.br: o host dela e o do Gmail. Sem este candidato a deducao
            // falha exatamente na caixa corporativa, que e o caso que motivou o vigia.
            var lista = ImapHostGuesser.Candidatos("ana@empresa.com.br");

            lista.Select(c => c.Host).Should().Equal(
                "imap.empresa.com.br",
                "mail.empresa.com.br",
                "imap.gmail.com");
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("sem-arroba.com")]
        [InlineData("@sodominio.com")]
        [InlineData("dois@@arrobas.com")]
        [InlineData("sem@dominio")]
        [InlineData("com espaco@dominio.com")]
        public void EnderecoRuim_NaoGeraCandidato(string endereco)
        {
            ImapHostGuesser.EnderecoParecevalido(endereco).Should().BeFalse();
            ImapHostGuesser.Candidatos(endereco).Should().BeEmpty();
            ImapHostGuesser.Primeiro(endereco).Should().BeNull();
        }

        [Fact]
        public void EnderecoComMaisPontos_EhValido()
        {
            // A validacao e frouxa de proposito: regra de e-mail completa e territorio de RFC
            // e recusa endereco valido. Quem valida de verdade e o servidor, no login.
            ImapHostGuesser.EnderecoParecevalido("nome.sobrenome+tag@sub.empresa.com.br")
                .Should().BeTrue();
        }
    }

    /// <summary>
    /// O cofre das senhas de app — §9 passo 2, com o LUGAR corrigido para a regra do projeto.
    /// </summary>
    public class MailVaultTests : IDisposable
    {
        private readonly string _raiz;

        public MailVaultTests()
        {
            // NUNCA o cofre real: escrever em ~/.AIB durante um ensaio mexeria nas senhas de
            // verdade do usuario.
            _raiz = Path.Combine(Path.GetTempPath(), "aib-cofre-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_raiz);
        }

        public void Dispose()
        {
            try { Directory.Delete(_raiz, true); } catch { }
        }

        private MailVault Novo() => new(_raiz);

        [Fact]
        public void OCofreFicaEmCredentialsMail_DentroDaPastaDoUsuario()
        {
            // A spec pedia %AppData%/AIB/secrets. Duas razoes para nao ser ali: a regra do
            // projeto manda tudo do usuario para ~/.AIB, e o reset de fabrica apaga
            // ~/.AIB/credentials inteiro — uma pasta IRMA chamada secrets sobreviveria ao
            // reset com as senhas dentro.
            var cofre = Novo();

            cofre.Pasta.Should().Be(Path.Combine(_raiz, "credentials", "mail"));
        }

        [Fact]
        public void GuardarELer_DevolveAMesmaSenha()
        {
            var cofre = Novo();

            cofre.Guardar("ana@empresa.com.br", "abcdefghijklmnop");

            cofre.Existe("ana@empresa.com.br").Should().BeTrue();
            cofre.Ler("ana@empresa.com.br").Should().Be("abcdefghijklmnop");
        }

        [Fact]
        public void OEnderecoNaoVaiParaONomeDoArquivo()
        {
            // A lista de arquivos de uma pasta e legivel por qualquer processo do usuario, e a
            // lista de enderecos nao precisa estar escrita ali para o cofre funcionar.
            var cofre = Novo();
            cofre.Guardar("ana@empresa.com.br", "x");

            var arquivos = Directory.GetFiles(cofre.Pasta).Select(Path.GetFileName).ToList();

            arquivos.Should().HaveCount(1);
            arquivos[0].Should().NotContain("ana").And.NotContain("@").And.NotContain("empresa");
            arquivos[0].Should().Be(MailVault.NomeDeArquivo("ana@empresa.com.br"));
        }

        [Fact]
        public void OMesmoEnderecoEmMaiusculas_EhAMesmaConta()
        {
            var cofre = Novo();
            cofre.Guardar("Ana@Empresa.com.br", "senha");

            cofre.Ler("ana@empresa.com.br").Should().Be("senha");
            Directory.GetFiles(cofre.Pasta).Should().HaveCount(1, "nao sao duas contas");
        }

        [Fact]
        public void SemBlob_LerDevolveNULL_ENaoUmaStringDeErro()
        {
            // O CredentialService devolve "ERRO: ...". Uma string de erro passa por qualquer
            // verificacao de preenchimento e acaba enviada ao servidor como se fosse senha.
            Novo().Ler("ninguem@lugar.com").Should().BeNull();
        }

        [Fact]
        public void Remover_ApagaOBlob()
        {
            var cofre = Novo();
            cofre.Guardar("ana@empresa.com.br", "senha");

            cofre.Remover("ana@empresa.com.br");

            cofre.Existe("ana@empresa.com.br").Should().BeFalse();
            cofre.Ler("ana@empresa.com.br").Should().BeNull();
        }

        [Fact]
        public void RemoverOQueNaoExiste_NaoExplode()
        {
            Novo().Remover("ninguem@lugar.com");
        }
    }

    /// <summary>
    /// As invariantes da lista de caixas — §7 A15 e A16.
    /// <para>
    /// Moram na coleção, e não na tela, porque adicionar, remover e promover são três caminhos
    /// distintos que podem quebrar as mesmas regras: escritas na View, seriam três cópias.
    /// </para>
    /// </summary>
    public class MailAccountListTests
    {
        private static MailAccount Conta(string endereco) => new() { Address = endereco };

        [Fact]
        public void APrimeiraConta_NASCE_Principal()
        {
            // Sem isto a lista ficaria sem principal nenhuma ate alguem clicar na estrela, e a
            // invariante "com a lista nao vazia, exatamente uma" comecaria violada.
            var lista = new MailAccountList();

            lista.Adicionar(Conta("a@x.com")).Ok.Should().BeTrue();

            lista.Principal.Should().NotBeNull();
            lista.Principal!.Address.Should().Be("a@x.com");
        }

        [Fact]
        public void ASegundaConta_NaoRoubaAPrincipal()
        {
            var lista = new MailAccountList();
            lista.Adicionar(Conta("a@x.com"));
            lista.Adicionar(Conta("b@x.com"));

            lista.Contas.Count(c => c.IsPrimary).Should().Be(1);
            lista.Principal!.Address.Should().Be("a@x.com");
        }

        [Theory]
        [InlineData("a@x.com")]
        [InlineData("A@X.COM")]
        [InlineData("  a@x.com  ")]
        public void EnderecoRepetido_EhRECUSADO(string repetido)
        {
            var lista = new MailAccountList();
            lista.Adicionar(Conta("a@x.com"));

            var r = lista.Adicionar(Conta(repetido));

            r.Ok.Should().BeFalse();
            r.Erro.Should().Contain("já está na lista");
            lista.Count.Should().Be(1);
        }

        [Fact]
        public void EnderecoInvalido_EhRecusado()
        {
            var r = new MailAccountList().Adicionar(Conta("sem-arroba"));

            r.Ok.Should().BeFalse();
            r.Erro.Should().Contain("inválido");
        }

        [Fact]
        public void APrincipal_NaoSAI_EnquantoHouverOutra()
        {
            // A principal e de onde sairia qualquer envio. Uma lista com tres contas e nenhuma
            // principal e um estado que nada na tela mostra e que so apareceria na hora de
            // enviar.
            var lista = new MailAccountList();
            lista.Adicionar(Conta("a@x.com"));
            lista.Adicionar(Conta("b@x.com"));

            var principal = lista.Principal!;

            lista.PodeRemover(principal).Should().BeFalse();
            var r = lista.Remover(principal);

            r.Ok.Should().BeFalse();
            r.Erro.Should().Contain("promova outra");
            lista.Count.Should().Be(2);
        }

        [Fact]
        public void AULTIMAConta_SaiMesmoSendoPrincipal()
        {
            // Aí a lista fica vazia, que é um estado desenhado (§3.12).
            var lista = new MailAccountList();
            lista.Adicionar(Conta("a@x.com"));

            lista.PodeRemover(lista.Principal!).Should().BeTrue();
            lista.Remover(lista.Contas[0]).Ok.Should().BeTrue();

            lista.Count.Should().Be(0);
            lista.Principal.Should().BeNull();
            lista.Configurado.Should().BeFalse();
        }

        [Fact]
        public void ANaoPrincipal_SaiSemCerimonia()
        {
            var lista = new MailAccountList();
            lista.Adicionar(Conta("a@x.com"));
            lista.Adicionar(Conta("b@x.com"));

            var segunda = lista.Contas[1];

            lista.PodeRemover(segunda).Should().BeTrue();
            lista.Remover(segunda).Ok.Should().BeTrue();
            lista.Count.Should().Be(1);
        }

        [Fact]
        public void Promover_MigraOSeloEDeixaExatamenteUma()
        {
            var lista = new MailAccountList();
            lista.Adicionar(Conta("a@x.com"));
            lista.Adicionar(Conta("b@x.com"));
            lista.Adicionar(Conta("c@x.com"));

            lista.TornarPrincipal(lista.Contas[2]).Ok.Should().BeTrue();

            lista.Contas.Count(c => c.IsPrimary).Should().Be(1);
            lista.Principal!.Address.Should().Be("c@x.com");
            lista.Contas[0].IsPrimary.Should().BeFalse("a antiga perde o selo");
        }

        [Fact]
        public void Configurado_DependeDeHaverSenhaNoCofre()
        {
            // É o que liga a aba de e-mails da janela de chat. Conta sem senha não conta.
            var lista = new MailAccountList();
            lista.Adicionar(Conta("a@x.com"));

            lista.Configurado.Should().BeFalse();

            lista.Contas[0].HasPassword = true;
            lista.Configurado.Should().BeTrue();
        }

        [Fact]
        public void Repovoar_CONSERTA_UmArquivoEditadoAMao()
        {
            // Duas principais, um endereco repetido e um invalido. Confiar no arquivo e so
            // validar na interface deixaria o estado invalido vivo ate a proxima mutacao.
            var lista = new MailAccountList();

            lista.Repovoar(new List<MailAccount>
            {
                new() { Address = "a@x.com", IsPrimary = true },
                new() { Address = "b@x.com", IsPrimary = true },
                new() { Address = "A@X.COM" },
                new() { Address = "lixo" }
            });

            lista.Count.Should().Be(2, "o repetido e o invalido nao entram");
            lista.Contas.Count(c => c.IsPrimary).Should().Be(1);
        }

        [Fact]
        public void Repovoar_SemNenhumaPrincipal_ELEGE_APrimeira()
        {
            var lista = new MailAccountList();

            lista.Repovoar(new List<MailAccount>
            {
                new() { Address = "a@x.com" },
                new() { Address = "b@x.com" }
            });

            lista.Principal!.Address.Should().Be("a@x.com");
        }
    }

    /// <summary>
    /// A persistência das caixas em <see cref="UserAppSettings"/>.
    /// </summary>
    public class MailAccountSettingsTests
    {
        [Fact]
        public void Clone_COPIA_AListaDeContas_ENaoAReferencia()
        {
            // Era copia rasa, e a justificativa escrita era "todos os campos sao string ou
            // tipo de valor". Deixou de valer com MailAccounts: o LoadSettings devolve um
            // clone do cache, entao quem mexesse na lista recebida estaria mexendo na lista do
            // cache — e na das outras janelas.
            var original = new UserAppSettings();
            original.MailAccounts.Add(new MailAccountSettings { Address = "a@x.com" });

            var copia = original.Clone();
            copia.MailAccounts.Add(new MailAccountSettings { Address = "b@x.com" });
            copia.MailAccounts[0].Address = "mexido@x.com";

            original.MailAccounts.Should().HaveCount(1);
            original.MailAccounts[0].Address.Should().Be("a@x.com");
        }

        [Fact]
        public void AConfiguracaoGravada_NaoTemSenhaNemEstado()
        {
            // Senha nunca passa por aqui: este objeto e serializado em JSON e clonado a cada
            // LoadSettings. Status e StatusText sao de tempo de execucao — gravar "Conectada"
            // e reler isso na abertura seguinte seria afirmar algo que ninguem verificou.
            var propriedades = typeof(MailAccountSettings).GetProperties().Select(p => p.Name).ToList();

            propriedades.Should().BeEquivalentTo(new[]
            {
                "Address", "ImapHost", "ImapPort", "UseSsl", "IsPrimary"
            });
        }
    }

    /// <summary>
    /// O estado.json — §Arquivos do vigia de e-mail.
    /// </summary>
    public class EstadoDasCaixasTests : IDisposable
    {
        private readonly string _raiz;

        public EstadoDasCaixasTests()
        {
            _raiz = Path.Combine(Path.GetTempPath(), "aib-estado-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_raiz);
        }

        public void Dispose()
        {
            try { Directory.Delete(_raiz, true); } catch { }
        }

        private EstadoDasCaixas Novo() => new(_raiz);

        [Fact]
        public void OEstadoMoraEmEmailEstadoJson_DentroDaPastaDoUsuario()
        {
            Novo().Caminho.Should().Be(Path.Combine(_raiz, "email", "estado.json"));
        }

        [Fact]
        public void GravarELer_DevolveOMesmoEstado()
        {
            var estado = Novo();

            estado.Gravar("ana@empresa.com.br", new EstadoDaCaixa
            {
                UidValidity = 8271,
                LastUid = 91043,
                LastReadUtc = new DateTime(2026, 9, 3, 12, 4, 0, DateTimeKind.Utc)
            });

            var lido = Novo().Ler("ana@empresa.com.br");

            lido.Should().NotBeNull();
            lido!.UidValidity.Should().Be(8271);
            lido.LastUid.Should().Be(91043);
        }

        [Fact]
        public void DuasCaixas_NaoSeMISTURAM()
        {
            var estado = Novo();
            estado.Gravar("a@x.com", new EstadoDaCaixa { UidValidity = 1, LastUid = 10 });
            estado.Gravar("b@x.com", new EstadoDaCaixa { UidValidity = 2, LastUid = 20 });

            estado.Ler("a@x.com")!.LastUid.Should().Be(10);
            estado.Ler("b@x.com")!.LastUid.Should().Be(20);
        }

        [Fact]
        public void AChaveEhOENDERECO_EmMinusculas()
        {
            // Chavear por rotulo abriria a classe de bug em que renomear a caixa na tela faz o
            // vigia perder o lastUid e retriar semanas de mensagem.
            var estado = Novo();
            estado.Gravar("Ana@Empresa.com.BR", new EstadoDaCaixa { UidValidity = 1, LastUid = 5 });

            estado.Ler("ana@empresa.com.br")!.LastUid.Should().Be(5);
        }

        [Fact]
        public void ValidadeIGUAL_PARTE_DoUltimoUid()
        {
            var estado = Novo();
            estado.Gravar("a@x.com", new EstadoDaCaixa { UidValidity = 8271, LastUid = 91043 });

            estado.UidDePartida("a@x.com", uidValidityAtual: 8271).Should().Be(91043);
        }

        [Fact]
        public void ValidadeDIFERENTE_RECOMECA_DoZero()
        {
            // O servidor renumerou a caixa: todo UID guardado e de outra numeracao. Confiar
            // nele seria pular mensagens achando que ja foram lidas.
            var estado = Novo();
            estado.Gravar("a@x.com", new EstadoDaCaixa { UidValidity = 8271, LastUid = 91043 });

            estado.UidDePartida("a@x.com", uidValidityAtual: 9999).Should().Be(0);
        }

        [Fact]
        public void CaixaNuncaLida_PARTE_DoZero()
        {
            Novo().UidDePartida("ninguem@lugar.com", 1).Should().Be(0);
        }

        [Fact]
        public void Remover_TiraSoAQuePediram()
        {
            var estado = Novo();
            estado.Gravar("a@x.com", new EstadoDaCaixa { UidValidity = 1, LastUid = 10 });
            estado.Gravar("b@x.com", new EstadoDaCaixa { UidValidity = 2, LastUid = 20 });

            estado.Remover("a@x.com");

            estado.Ler("a@x.com").Should().BeNull();
            estado.Ler("b@x.com").Should().NotBeNull();
        }

        [Fact]
        public void ArquivoCORROMPIDO_NaoDerrubaAVarredura()
        {
            // Perder o estado custa uma releitura. Derrubar a varredura custaria a varredura.
            var estado = Novo();
            Directory.CreateDirectory(Path.GetDirectoryName(estado.Caminho)!);
            File.WriteAllText(estado.Caminho, "{ isto nao e json");

            estado.Ler("a@x.com").Should().BeNull();
            estado.UidDePartida("a@x.com", 1).Should().Be(0);
        }
    }

    /// <summary>
    /// A tradução do erro do servidor para uma frase acionável.
    /// </summary>
    public class MensagemDeAutenticacaoTests
    {
        [Theory]
        [InlineData("[ALERT] IMAP access is disabled for your domain.", "IMAP está desligado")]
        [InlineData("Application-specific password required", "exige senha de app")]
        [InlineData("Your account has been disabled by administrator", "bloqueada pelo administrador")]
        [InlineData("Invalid credentials (Failure)", "senha de app recusada")]
        [InlineData("", "senha de app recusada")]
        public void CadaCausa_LEVA_AUmConsertoDiferente(string doServidor, string esperado)
        {
            // Os tres modos de falha chegam como o mesmo tipo de excecao. Mandar "senha
            // recusada" nos tres manda o usuario gerar senha nova quando o problema era o IMAP
            // desligado — e ele nunca vai descobrir isso sozinho.
            MailKitMailService.MensagemDeAutenticacao(doServidor).Should().Contain(esperado);
        }
    }

    /// <summary>O esqueleto do serviço de e-mail, enquanto não há IMAP.</summary>
    public class MailServiceStubTests
    {
        [Fact]
        public void OEsqueletoAceita_MasNUNCA_MarcaComoVerificado()
        {
            // §7 A15 pede que so entre conta cujo login passou. Enquanto nao ha IMAP, o desvio
            // fica VISIVEL na tela: a linha nasce ambar com "verificacao pendente", nunca
            // verde. Nao ha como confundir conta aceita pelo esqueleto com conta conectada.
            var r = new MailServiceStub()
                .TestLoginAsync("ana@gmail.com", "senha", default).Result;

            r.Ok.Should().BeTrue();
            r.Verificado.Should().BeFalse();
            r.Endpoint.Host.Should().Be("imap.gmail.com");
        }

        [Fact]
        public void SemDominioUtilizavel_OEsqueletoRECUSA()
        {
            var r = new MailServiceStub().TestLoginAsync("lixo", "senha", default).Result;

            r.Ok.Should().BeFalse();
            r.Erro.Should().Contain("não encontramos o servidor");
        }
    }
}
