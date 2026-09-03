using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AIB.Services.Mail;

/// <summary>
/// Cofre das senhas de app, uma por caixa — §9 passo 2 de tela-configuracoes, corrigido
/// quanto ao LUGAR.
/// <para>
/// A spec pedia <c>%AppData%/AIB/secrets/</c>. Aqui é
/// <c>~/.AIB/credentials/mail/</c>, e por duas razões. A primeira é a regra do projeto: tudo
/// que é do usuário mora em <c>~/.AIB</c> — o histórico de chat já foi movido de
/// <c>%APPDATA%\AIB</c> por ser o último arquivo que tinha ficado para trás. A segunda é o
/// reset de fábrica: ele apaga <c>~/.AIB/credentials</c> inteiro, então uma pasta IRMÃ
/// chamada <c>secrets</c> sobreviveria ao reset com as senhas dentro.
/// </para>
/// <para>
/// Não usa o <see cref="CredentialService"/> apesar de ele já cifrar com DPAPI, e o motivo é
/// concreto: quando não acha o arquivo do sistema pedido, ele VARRE os outros e devolve o
/// primeiro valor com a mesma chave. Para senha de e-mail isso é devolver silenciosamente a
/// credencial errada. Ele também imprime o nome da chave no console. As duas coisas são
/// aceitáveis onde ele já é usado e não são aqui.
/// </para>
/// </summary>
public sealed class MailVault
{
    private readonly string _raiz;

    /// <param name="raizDeDados">
    /// Sobrescreve <c>~/.AIB</c>. Existe para os ensaios: escrever no cofre real do usuário
    /// durante um teste é inaceitável.
    /// </param>
    public MailVault(string? raizDeDados = null)
    {
        string baseDir = string.IsNullOrWhiteSpace(raizDeDados)
            ? DirectoryService.DataDir
            : raizDeDados!;

        _raiz = Path.Combine(baseDir, "credentials", "mail");
    }

    /// <summary>Pasta do cofre. Diagnóstico e ensaio.</summary>
    public string Pasta => _raiz;

    /// <summary>
    /// Nome do arquivo de uma conta: SHA-256 do endereço em minúsculas, em hexadecimal.
    /// <para>
    /// O endereço NÃO vira nome de arquivo. Primeiro porque ele tem <c>@</c> e pontos e viraria
    /// um nome frágil; segundo, e mais importante, porque a lista de arquivos de uma pasta é
    /// legível por qualquer processo do usuário — e a lista de endereços não precisa estar
    /// escrita ali para o cofre funcionar.
    /// </para>
    /// </summary>
    public static string NomeDeArquivo(string endereco)
    {
        byte[] bytes = Encoding.UTF8.GetBytes((endereco ?? "").Trim().ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ".bin";
    }

    private string CaminhoDe(string endereco) => Path.Combine(_raiz, NomeDeArquivo(endereco));

    /// <summary>Grava a senha de app cifrada com DPAPI, escopo do usuário atual.</summary>
    public void Guardar(string endereco, string senha)
    {
        if (string.IsNullOrWhiteSpace(endereco)) throw new ArgumentException("endereço vazio", nameof(endereco));
        if (senha == null) throw new ArgumentNullException(nameof(senha));

        Directory.CreateDirectory(_raiz);

        byte[] claro = Encoding.UTF8.GetBytes(senha);
        byte[] cifrado = ProtectedData.Protect(claro, null, DataProtectionScope.CurrentUser);
        Array.Clear(claro, 0, claro.Length);

        File.WriteAllBytes(CaminhoDe(endereco), cifrado);
    }

    /// <summary>
    /// A senha, ou <c>null</c> se não há blob ou ele não decifra.
    /// <para>
    /// Devolve NULL em vez de uma string de erro. O <see cref="CredentialService"/> devolve
    /// <c>"ERRO: ..."</c>, e uma string de erro passa por qualquer verificação de
    /// preenchimento e acaba enviada ao servidor como se fosse senha.
    /// </para>
    /// </summary>
    public string? Ler(string endereco)
    {
        try
        {
            string caminho = CaminhoDe(endereco);
            if (!File.Exists(caminho)) return null;

            byte[] cifrado = File.ReadAllBytes(caminho);
            byte[] claro = ProtectedData.Unprotect(cifrado, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(claro);
        }
        catch
        {
            // Blob de outra máquina ou de outro usuário do Windows não decifra. Não é erro a
            // relatar: é "não tem senha", e o fluxo de "Alterar senha de app" resolve.
            return null;
        }
    }

    public bool Existe(string endereco)
    {
        try { return File.Exists(CaminhoDe(endereco)); }
        catch { return false; }
    }

    /// <summary>
    /// Apaga o blob. Chamado pelo MESMO comando que remove a conta da lista — §9 passo 2: uma
    /// conta removida que deixasse a senha para trás seria credencial órfã em disco, sem nada
    /// na interface que a mencionasse.
    /// </summary>
    public void Remover(string endereco)
    {
        try
        {
            string caminho = CaminhoDe(endereco);
            if (File.Exists(caminho)) File.Delete(caminho);
        }
        catch
        {
            // Arquivo travado: a conta sai da lista de todo jeito. Deixar a conta na tela por
            // causa de um handle preso seria pior que o blob sobrando.
        }
    }
}
