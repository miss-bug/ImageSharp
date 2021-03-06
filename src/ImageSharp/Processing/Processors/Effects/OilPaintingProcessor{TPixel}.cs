// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Advanced.ParallelUtils;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;

namespace SixLabors.ImageSharp.Processing.Processors.Effects
{
    /// <summary>
    /// Applies oil painting effect processing to the image.
    /// </summary>
    /// <remarks>Adapted from <see href="https://softwarebydefault.com/2013/06/29/oil-painting-cartoon-filter/"/> by Dewald Esterhuizen.</remarks>
    /// <typeparam name="TPixel">The pixel format.</typeparam>
    internal class OilPaintingProcessor<TPixel> : ImageProcessor<TPixel>
        where TPixel : struct, IPixel<TPixel>
    {
        private readonly OilPaintingProcessor definition;

        /// <summary>
        /// Initializes a new instance of the <see cref="OilPaintingProcessor{TPixel}"/> class.
        /// </summary>
        /// <param name="configuration">The configuration which allows altering default behaviour or extending the library.</param>
        /// <param name="definition">The <see cref="OilPaintingProcessor"/> defining the processor parameters.</param>
        /// <param name="source">The source <see cref="Image{TPixel}"/> for the current processor instance.</param>
        /// <param name="sourceRectangle">The source area to process for the current processor instance.</param>
        public OilPaintingProcessor(Configuration configuration, OilPaintingProcessor definition, Image<TPixel> source, Rectangle sourceRectangle)
            : base(configuration, source, sourceRectangle)
        {
            this.definition = definition;
        }

        /// <inheritdoc/>
        protected override void OnFrameApply(ImageFrame<TPixel> source)
        {
            int brushSize = this.definition.BrushSize;
            if (brushSize <= 0 || brushSize > source.Height || brushSize > source.Width)
            {
                throw new ArgumentOutOfRangeException(nameof(brushSize));
            }

            int startY = this.SourceRectangle.Y;
            int endY = this.SourceRectangle.Bottom;
            int startX = this.SourceRectangle.X;
            int endX = this.SourceRectangle.Right;
            int maxY = endY - 1;
            int maxX = endX - 1;

            int radius = brushSize >> 1;
            int levels = this.definition.Levels;
            int rowWidth = source.Width;
            int rectangleWidth = this.SourceRectangle.Width;

            Configuration configuration = this.Configuration;

            using Buffer2D<TPixel> targetPixels = this.Configuration.MemoryAllocator.Allocate2D<TPixel>(source.Size());
            source.CopyTo(targetPixels);

            var workingRect = Rectangle.FromLTRB(startX, startY, endX, endY);
            ParallelHelper.IterateRows(
                workingRect,
                this.Configuration,
                (rows) =>
                {
                    /* Allocate the two temporary Vector4 buffers, one for the source row and one for the target row.
                     * The ParallelHelper.IterateRowsWithTempBuffers overload is not used in this case because
                     * the two allocated buffers have a length equal to the width of the source image,
                     * and not just equal to the width of the target rectangle to process.
                     * Furthermore, there are two buffers being allocated in this case, so using that overload would
                     * have still required the explicit allocation of the secondary buffer.
                     * Similarly, one temporary float buffer is also allocated from the pool, and that is used
                     * to create the target bins for all the color channels being processed.
                     * This buffer is only rented once outside of the main processing loop, and its contents
                     * are cleared for each loop iteration, to avoid the repeated allocation for each processed pixel. */
                    using (IMemoryOwner<Vector4> sourceRowBuffer = configuration.MemoryAllocator.Allocate<Vector4>(rowWidth))
                    using (IMemoryOwner<Vector4> targetRowBuffer = configuration.MemoryAllocator.Allocate<Vector4>(rowWidth))
                    using (IMemoryOwner<float> bins = configuration.MemoryAllocator.Allocate<float>(levels * 4))
                    {
                        Span<Vector4> sourceRowVector4Span = sourceRowBuffer.Memory.Span;
                        Span<Vector4> sourceRowAreaVector4Span = sourceRowVector4Span.Slice(startX, rectangleWidth);

                        Span<Vector4> targetRowVector4Span = targetRowBuffer.Memory.Span;
                        Span<Vector4> targetRowAreaVector4Span = targetRowVector4Span.Slice(startX, rectangleWidth);

                        ref float binsRef = ref bins.GetReference();
                        ref int intensityBinRef = ref Unsafe.As<float, int>(ref binsRef);
                        ref float redBinRef = ref Unsafe.Add(ref binsRef, levels);
                        ref float blueBinRef = ref Unsafe.Add(ref redBinRef, levels);
                        ref float greenBinRef = ref Unsafe.Add(ref blueBinRef, levels);

                        for (int y = rows.Min; y < rows.Max; y++)
                        {
                            Span<TPixel> sourceRowPixelSpan = source.GetPixelRowSpan(y);
                            Span<TPixel> sourceRowAreaPixelSpan = sourceRowPixelSpan.Slice(startX, rectangleWidth);

                            PixelOperations<TPixel>.Instance.ToVector4(configuration, sourceRowAreaPixelSpan, sourceRowAreaVector4Span);

                            for (int x = startX; x < endX; x++)
                            {
                                int maxIntensity = 0;
                                int maxIndex = 0;

                                // Clear the current shared buffer before processing each target pixel
                                bins.Memory.Span.Clear();

                                for (int fy = 0; fy <= radius; fy++)
                                {
                                    int fyr = fy - radius;
                                    int offsetY = y + fyr;

                                    offsetY = offsetY.Clamp(0, maxY);

                                    Span<TPixel> sourceOffsetRow = source.GetPixelRowSpan(offsetY);

                                    for (int fx = 0; fx <= radius; fx++)
                                    {
                                        int fxr = fx - radius;
                                        int offsetX = x + fxr;
                                        offsetX = offsetX.Clamp(0, maxX);

                                        var vector = sourceOffsetRow[offsetX].ToVector4();

                                        float sourceRed = vector.X;
                                        float sourceBlue = vector.Z;
                                        float sourceGreen = vector.Y;

                                        int currentIntensity = (int)MathF.Round((sourceBlue + sourceGreen + sourceRed) / 3F * (levels - 1));

                                        Unsafe.Add(ref intensityBinRef, currentIntensity)++;
                                        Unsafe.Add(ref redBinRef, currentIntensity) += sourceRed;
                                        Unsafe.Add(ref blueBinRef, currentIntensity) += sourceBlue;
                                        Unsafe.Add(ref greenBinRef, currentIntensity) += sourceGreen;

                                        if (Unsafe.Add(ref intensityBinRef, currentIntensity) > maxIntensity)
                                        {
                                            maxIntensity = Unsafe.Add(ref intensityBinRef, currentIntensity);
                                            maxIndex = currentIntensity;
                                        }
                                    }

                                    float red = MathF.Abs(Unsafe.Add(ref redBinRef, maxIndex) / maxIntensity);
                                    float blue = MathF.Abs(Unsafe.Add(ref blueBinRef, maxIndex) / maxIntensity);
                                    float green = MathF.Abs(Unsafe.Add(ref greenBinRef, maxIndex) / maxIntensity);
                                    float alpha = sourceRowVector4Span[x].W;

                                    targetRowVector4Span[x] = new Vector4(red, green, blue, alpha);
                                }
                            }

                            Span<TPixel> targetRowAreaPixelSpan = targetPixels.GetRowSpan(y).Slice(startX, rectangleWidth);

                            PixelOperations<TPixel>.Instance.FromVector4Destructive(configuration, targetRowAreaVector4Span, targetRowAreaPixelSpan);
                        }
                    }
                });

            Buffer2D<TPixel>.SwapOrCopyContent(source.PixelBuffer, targetPixels);
        }
    }
}
