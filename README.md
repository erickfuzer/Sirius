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
| `F5` | Executar ou parar o projeto |
| `Ctrl J` | Abrir ou fechar o terminal |
| `Ctrl +` e `Ctrl -` | Aumentar ou diminuir a fonte do editor (`Ctrl 0` volta ao padrão) |
| `F2` | Renomear o item selecionado na lista de arquivos |

## Testes

```bash
dotnet test
```

## Fontes

Geist e IBM Plex Mono são distribuídas sob a licença SIL Open Font. As licenças estão em `src/Sirius/Assets/Fonts`.
