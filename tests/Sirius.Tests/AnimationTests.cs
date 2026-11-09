using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SkiaSharp;
using Sirius.Core;

namespace Sirius.Tests;

public class AnimationTests
{
    // Um GIF pequeno feito à mão: cada quadro é uma cor só da paleta (0 vermelho, 1 verde, 2 azul, 3 preto).
    // loop null não põe a extensão de repetição, e o GIF toca uma vez só.
    public static byte[] Gif(int width, int height, int[] colors, int[] centiseconds, int? loop = 0)
    {
        var bytes = new List<byte>();
        void Short(int value) { bytes.Add((byte)(value & 0xFF)); bytes.Add((byte)(value >> 8)); }

        bytes.AddRange("GIF89a"u8.ToArray());
        Short(width);
        Short(height);
        bytes.AddRange([0x91, 0, 0]);
        bytes.AddRange([255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0]);
        if (loop is { } count)
        {
            bytes.AddRange([0x21, 0xFF, 0x0B]);
            bytes.AddRange("NETSCAPE2.0"u8.ToArray());
            bytes.AddRange([0x03, 0x01]);
            Short(count);
            bytes.Add(0);
        }

        for (var f = 0; f < colors.Length; f++)
        {
            bytes.AddRange([0x21, 0xF9, 0x04, 0x04]);
            Short(centiseconds[f]);
            bytes.AddRange([0, 0]);
            bytes.Add(0x2C);
            Short(0);
            Short(0);
            Short(width);
            Short(height);
            bytes.Add(0);

            // LZW sem compressão: um código "limpar" antes de cada pixel mantém todos os códigos com 3 bits.
            var codes = new List<int>();
            for (var i = 0; i < width * height; i++)
            {
                codes.Add(4);
                codes.Add(colors[f]);
            }
            codes.Add(5);
            var packed = new List<byte>();
            int buffer = 0, bits = 0;
            foreach (var code in codes)
            {
                buffer |= code << bits;
                bits += 3;
                while (bits >= 8)
                {
                    packed.Add((byte)(buffer & 0xFF));
                    buffer >>= 8;
                    bits -= 8;
                }
            }
            if (bits > 0)
                packed.Add((byte)buffer);

            bytes.Add(2);
            for (var i = 0; i < packed.Count; i += 255)
            {
                var size = Math.Min(255, packed.Count - i);
                bytes.Add((byte)size);
                bytes.AddRange(packed.Skip(i).Take(size));
            }
            bytes.Add(0);
        }
        bytes.Add(0x3B);
        return bytes.ToArray();
    }

    private static MainWindow Open(TempFolder temp, TempFolder settingsFolder)
    {
        var window = new MainWindow(new AppSettings(), Path.Combine(settingsFolder.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static async Task Until(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "A condição não aconteceu a tempo.");
            await Task.Delay(15);
            Dispatcher.UIThread.RunJobs();
        }
    }

    // Sem janela

    [Fact]
    public void Monta_cada_quadro_com_o_tempo_e_a_repeticao()
    {
        using var frames = ImageFile.DecodeFrames(Gif(4, 3, [0, 1, 2], [5, 20, 7]))!;

        Assert.Equal(3, frames.Frames.Count);
        Assert.Equal(new[] { 50, 200, 70 }, frames.Durations);
        Assert.True(frames.Repetitions < 0);
        Assert.Equal(new SKColor(255, 0, 0), frames.Frames[0].GetPixel(3, 2));
        Assert.Equal(new SKColor(0, 255, 0), frames.Frames[1].GetPixel(0, 0));
        Assert.Equal(new SKColor(0, 0, 255), frames.Frames[2].GetPixel(2, 1));
    }

    [Fact]
    public void Sem_extensao_de_repeticao_toca_uma_vez()
    {
        using var frames = ImageFile.DecodeFrames(Gif(2, 2, [0, 1], [5, 5], loop: null))!;
        Assert.Equal(0, frames.Repetitions);
    }

    [Fact]
    public void Imagem_de_um_quadro_nao_anima()
    {
        Assert.Null(ImageFile.DecodeFrames(Gif(2, 2, [0], [5])));
    }

    [Fact]
    public void Quadro_rapido_demais_dura_cem_milissegundos()
    {
        Assert.Equal(100, ImageFile.FrameDuration(0));
        Assert.Equal(100, ImageFile.FrameDuration(10));
        Assert.Equal(20, ImageFile.FrameDuration(20));
    }

    [Fact]
    public void Animacao_grande_demais_avisa()
    {
        var error = Assert.Throws<ImageFileException>(() => ImageFile.DecodeFrames(Gif(4, 4, [0, 1, 2], [5, 5, 5]), maxBytes: 100));
        Assert.Contains("3 quadros", error.Message);
    }

    [Fact]
    public void So_gif_e_webp_podem_animar()
    {
        Assert.True(ImageFile.CanAnimate("a.GIF"));
        Assert.True(ImageFile.CanAnimate("a.webp"));
        Assert.False(ImageFile.CanAnimate("a.png"));
    }

    // Na janela

    [AvaloniaFact]
    public async Task Gif_animado_anima_na_aba_e_para_ao_trocar()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var gif = temp.File("gato.gif");
        File.WriteAllBytes(gif, Gif(8, 8, [0, 1, 2], [2, 2, 2]));
        var text = temp.File("a.txt", "x");
        var window = Open(temp, settings);

        window.OpenFile(gif);
        var preview = window.ImagePreviewControl;
        await Until(() => preview.FrameCount == 3);
        Assert.True(preview.IsPlaying);
        await Until(() => preview.FrameIndex == 2);
        // Repete sem fim: volta ao primeiro.
        await Until(() => preview.FrameIndex == 0);

        window.OpenFile(text);
        Dispatcher.UIThread.RunJobs();
        Assert.False(preview.IsPlaying);
        Assert.Equal(0, preview.FrameCount);

        window.OpenFile(gif);
        await Until(() => preview.FrameCount == 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Botao_pausa_continua_e_some_fora_da_imagem()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var gif = temp.File("gato.gif");
        File.WriteAllBytes(gif, Gif(8, 8, [0, 1, 2], [3, 3, 3]));
        var text = temp.File("a.txt", "x");
        var window = Open(temp, settings);

        window.OpenFile(gif);
        var preview = window.ImagePreviewControl;
        await Until(() => preview.FrameCount == 3);
        Assert.True(window.IsAnimationToggleVisible);
        Assert.Equal("Pausar", window.AnimationToggleText);

        window.ToggleAnimation();
        Assert.False(preview.IsPlaying);
        Assert.Equal("Continuar", window.AnimationToggleText);
        var frame = preview.FrameIndex;
        await Task.Delay(150);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(frame, preview.FrameIndex);

        window.ToggleAnimation();
        Assert.True(preview.IsPlaying);
        Assert.Equal("Pausar", window.AnimationToggleText);
        // Continua de onde parou e volta a andar.
        await Until(() => preview.FrameIndex != frame);

        window.OpenFile(text);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsAnimationToggleVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Depois_do_fim_o_botao_repete()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var gif = temp.File("uma-vez.gif");
        File.WriteAllBytes(gif, Gif(8, 8, [0, 1, 2], [2, 2, 2], loop: null));
        var window = Open(temp, settings);

        window.OpenFile(gif);
        var preview = window.ImagePreviewControl;
        await Until(() => preview.IsFinished);
        Assert.Equal("Repetir", window.AnimationToggleText);

        window.ToggleAnimation();
        Assert.Equal(0, preview.FrameIndex);
        Assert.True(preview.IsPlaying);
        await Until(() => preview.IsFinished);
        Assert.Equal(2, preview.FrameIndex);
        window.Close();
    }

    [AvaloniaFact]
    public void Imagem_parada_nao_mostra_o_botao()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var gif = temp.File("parado.gif");
        File.WriteAllBytes(gif, Gif(8, 8, [0], [3]));
        var window = Open(temp, settings);

        window.OpenFile(gif);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsAnimationToggleVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Gif_sem_repeticao_para_no_ultimo_quadro()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var gif = temp.File("uma-vez.gif");
        File.WriteAllBytes(gif, Gif(8, 8, [0, 1, 2], [2, 2, 2], loop: null));
        var window = Open(temp, settings);

        window.OpenFile(gif);
        var preview = window.ImagePreviewControl;
        await Until(() => preview.FrameCount == 3);
        await Until(() => !preview.IsPlaying);
        Assert.Equal(2, preview.FrameIndex);
        window.Close();
    }
}
