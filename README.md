# Sirius

IDE nativa para Windows, feita em C# com Avalonia, sem WebView nem Electron.

## Como rodar

Precisa do [SDK do .NET 10](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/Sirius
```

## Atalhos

| Atalho | Ação |
|---|---|
| `Ctrl K` | Barra de comando: encontrar arquivos e executar ações |
| `Ctrl O` | Abrir pasta |
| `Ctrl S` | Salvar |
| `Ctrl W` | Fechar aba |
| `Ctrl F` | Procurar no arquivo |
| `Ctrl Shift F` | Buscar em todo o projeto (`Enter` avança pelos resultados) |
| `Ctrl .` | Modo foco |

## Testes

```bash
dotnet test
```

## Fontes

Geist e Newsreader são distribuídas sob a licença SIL Open Font. As licenças estão em `src/Sirius/Assets/Fonts`.
