using AIB.Services;

namespace AIB.Services.Ai;

/// <summary>Fábrica de providers dirigida pelas settings do usuário.</summary>
public interface IChatProviderFactory
{
    /// <summary>
    /// Devolve o provider correspondente às settings atuais. Reaproveita a instância
    /// anterior enquanto provider, modelo, URL e credencial efetiva não mudarem — a
    /// credencial entra na comparação porque o sentinela "use-vault" não muda quando o
    /// usuário troca a chave no cofre. Deve ser thread-safe: o aquecimento e um turno
    /// podem correr juntos na inicialização.
    /// </summary>
    IChatProvider GetProvider(UserAppSettings settings);
}
