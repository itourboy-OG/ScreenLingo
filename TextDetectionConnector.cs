using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ScreenLingo;

/// <summary>Checks OCR candidates against the bundled PP-OCRv5 scene-text probability map.</summary>
public sealed class TextDetectionConnector : IDisposable
{
    private readonly string modelPath;
    private InferenceSession? session;
    private CapturedImage? previousImage;
    private float[] probabilities = [];
    private int width;
    private int height;

    public TextDetectionConnector(string modelPath) => this.modelPath = modelPath;

    public ImmutableArray<TextRegion> Filter(CapturedImage image, ImmutableArray<TextRegion> regions)
    {
        if (!ReferenceEquals(previousImage, image))
        {
            session ??= CreateSession(modelPath);
            using MemoryStream input = new(image.Png);
            BitmapFrame frame = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            double ratio = Math.Min(1, 1920d / Math.Max(image.Width, image.Height));
            width = Math.Max(32, (int)Math.Round(image.Width * ratio / 32) * 32);
            height = Math.Max(32, (int)Math.Round(image.Height * ratio / 32) * 32);
            TransformedBitmap resized = new(frame, new ScaleTransform(width / (double)image.Width, height / (double)image.Height));
            FormatConvertedBitmap bitmap = new(resized, PixelFormats.Bgr24, null, 0);
            byte[] pixels = new byte[checked(width * height * 3)];
            bitmap.CopyPixels(pixels, width * 3, 0);
            DenseTensor<float> tensor = new(new[] { 1, 3, height, width });
            for (int channel = 0; channel < 3; channel++)
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                tensor[0, channel, y, x] = pixels[(y * width + x) * 3 + channel] / 127.5f - 1;
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> output = session.Run(
                new[] { NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.Single(), tensor) });
            Tensor<float> map = output.First().AsTensor<float>();
            if (map.Dimensions.Length != 4 || map.Dimensions[0] != 1 || map.Dimensions[1] != 1)
                throw new InvalidDataException("The bundled scene-text detector returned an unexpected probability-map shape.");
            height = map.Dimensions[2]; width = map.Dimensions[3];
            probabilities = map.ToArray();
            previousImage = image;
        }
        return regions.Where(region => IsText(image, region)).ToImmutableArray();
    }

    private bool IsText(CapturedImage image, TextRegion region)
    {
        PixelRect bounds = region.Bounds;
        if (bounds.Width < 4 || bounds.Height < 6) return false;
        int left = Math.Clamp(bounds.X * width / image.Width, 0, width - 1);
        int top = Math.Clamp(bounds.Y * height / image.Height, 0, height - 1);
        int right = Math.Clamp((bounds.X + bounds.Width) * width / image.Width, left + 1, width);
        int bottom = Math.Clamp((bounds.Y + bounds.Height) * height / image.Height, top + 1, height);
        int textPixels = 0;
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
            if (probabilities[y * width + x] >= 0.5f) textPixels++;
        return textPixels >= (right - left) * (bottom - top) * 0.25;
    }

    private static InferenceSession CreateSession(string path)
    {
        using SessionOptions options = new() { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
        return new InferenceSession(path, options);
    }

    public void Dispose() => session?.Dispose();
}
