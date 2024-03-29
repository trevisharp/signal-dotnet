using System;
using System.Buffers;
using System.Threading.Tasks;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics.Arm;

namespace Signals.Internal;

internal static class FourrierTransform
{
    private const int dftThreshold = 32;
    private const int sampleSize = 1024;
    private const int halfSampleSize = sampleSize / 2;

    private static float[] reAux = null;
    private static float[] imAux = null;
    private static float[] cosBuffer = null;
    private static float[] sinBuffer = null;
    private static float[] cosSamples = null;
    private static float[] sinSamples = null;

    internal static void RFFT(float[] real, float[] imaginary)
    {
        throw new NotImplementedException();
    }

    internal static void IRFFT(float[] real, float[] imaginary)
    {
        throw new NotImplementedException();
    }

    internal static void STFFT(float[] real, float[] imaginary)
    {
        throw new NotImplementedException();
    }

    internal static void IFFT(float[] real, float[] imaginary)
    {
        if (real.Length != imaginary.Length)
            throw new Exception("Real and Imaginary Signal must have the same size.");
        
        if (!testPowerOfTwo(real.Length))
            throw new Exception("Signals must have a power size of 2.");
            
        initAuxBuffers(real.Length);

        ifft(real, reAux, imaginary, imAux);
    }

    internal static void FFT(float[] real, float[] imaginary)
    {
        if (real.Length != imaginary.Length)
            throw new Exception("Real and Imaginary Signal must have the same size.");
        
        if (!testPowerOfTwo(real.Length))
            throw new Exception("Signals must have a power size of 2.");

        initAuxBuffers(real.Length);

        fft(real, reAux, imaginary, imAux);
    }

    private static bool testPowerOfTwo(int N)
    {
        do
        {
            N /= 2;
            if (N % 2 == 1 && N > 1)
                return false;
        } while (N > 0);
        return true;
    }
    
    private static void initAuxBuffers(int size)
    {
        if (reAux == null)
        {
            rentAuxBuffers(size);
            return;
        }

        if (reAux.Length < size)
        {
            ArrayPool<float>.Shared.Return(reAux);
            ArrayPool<float>.Shared.Return(imAux);
            rentAuxBuffers(size);
        }
    }

    private static void rentAuxBuffers(int size)
    {
        reAux = ArrayPool<float>.Shared.Rent(size);
        imAux = ArrayPool<float>.Shared.Rent(size);
    }

    private static void ifft(
        float[] reBuffer,
        float[] reAux,
        float[] imBuffer,
        float[] imAux
    )
    {
        int N = reBuffer.Length;
        int sectionCount = N / dftThreshold;

        evenOddFragmentation(N, dftThreshold, sectionCount, reBuffer, imBuffer, reAux, imAux);
        
        var cosBuffer = getCosBuffer(dftThreshold);
        var sinBuffer = getSinBuffer(dftThreshold);

        fracIDFT(reAux, imAux, reBuffer, imBuffer, cosBuffer, sinBuffer, sectionCount);

        mergeIDFTresults(sectionCount, dftThreshold, reBuffer, imBuffer, reAux, imAux);

        normalize(reBuffer);
        normalize(imBuffer);
    }

    private static void normalize(float[] data)
    {
        int N = data.Length;
        for (int i = 0; i < data.Length; i++)
            data[i] = data[i] / N;
    }

    private static void fft(
        float[] reBuffer,
        float[] reAux,
        float[] imBuffer,
        float[] imAux
    )
    {
        int N = reBuffer.Length;
        int sectionCount = N / dftThreshold;

        var cosBuffer = getCosBuffer(dftThreshold);
        var sinBuffer = getSinBuffer(dftThreshold);
        generateSamples();
        
        evenOddFragmentation(N, dftThreshold, sectionCount, reBuffer, imBuffer, reAux, imAux);

        fracDFT(reAux, imAux, reBuffer, imBuffer, cosBuffer, sinBuffer, sectionCount);

        mergeDFTresults(sectionCount, dftThreshold, reBuffer, imBuffer, reAux, imAux);
    }

    private static void evenOddFragmentation(
        int N, int division, int sectionCount,
        float[] reBuffer, float[] imBuffer,
        float[] reAux, float[] imAux
    )
    {
        int[] coefs = getFFTCoefs(N, division);
        for (int i = 0; i < N; i++)
        {
            int sec = (i / dftThreshold);
            int idSec = (i % dftThreshold);
            int index = sectionCount * idSec + coefs[sec];
            reAux[i] = reBuffer[index];
            imAux[i] = imBuffer[index];
        }
    }

    private static void fracIDFT(
        float[] reAux, float[] imAux,
        float[] reBuffer, float[] imBuffer,
        float[] cosBuffer, float[] sinBuffer,
        int sectionCount)
    {
        if (sectionCount == 1)
            slowIDFT(
                reAux, imAux, reBuffer, imBuffer,
                cosBuffer, sinBuffer, 0, dftThreshold
            );
        else if (Environment.ProcessorCount == 1 || sectionCount == 2)
            sequentialFracIDFT(
                reAux, imAux, reBuffer, imBuffer, 
                cosBuffer, sinBuffer, sectionCount
            );
        else
            parallelFracIDFT(
                reAux, imAux, reBuffer, imBuffer, 
                cosBuffer, sinBuffer, sectionCount
            );
    }

    private static void fracDFT(
        float[] reAux, float[] imAux,
        float[] reBuffer, float[] imBuffer,
        float[] cosBuffer, float[] sinBuffer,
        int sectionCount)
    {
        if (sectionCount == 1)
            slowDFT(
                reAux, imAux, reBuffer, imBuffer,
                cosBuffer, sinBuffer, 0, dftThreshold
            );
        else if (Environment.ProcessorCount == 1 || sectionCount == 2)
            sequentialFracDFT(
                reAux, imAux, reBuffer, imBuffer, 
                cosBuffer, sinBuffer, sectionCount
            );
        else
            parallelFracDFT(
                reAux, imAux, reBuffer, imBuffer, 
                cosBuffer, sinBuffer, sectionCount
            );
    }

    private static void sequentialFracDFT(
        float[] reAux, float[] imAux,
        float[] reBuffer, float[] imBuffer,
        float[] cosBuffer, float[] sinBuffer,
        int sectionCount)
    {
        for (int i = 0; i < sectionCount; i++)
        {
            dft(
                reAux, imAux, reBuffer, imBuffer, 
                cosBuffer, sinBuffer,
                i * dftThreshold, dftThreshold
            );
        }
    }

    private static void parallelFracDFT(
        float[] reAux, float[] imAux,
        float[] reBuffer, float[] imBuffer,
        float[] cosBuffer, float[] sinBuffer,
        int sectionCount)
    {
        Parallel.For(0, sectionCount, i =>
        {
            dft(
                reAux, imAux, reBuffer, imBuffer, 
                cosBuffer, sinBuffer,
                i * dftThreshold, dftThreshold
            );
        });
    }

    private static void sequentialFracIDFT(
        float[] reAux, float[] imAux,
        float[] reBuffer, float[] imBuffer,
        float[] cosBuffer, float[] sinBuffer,
        int sectionCount)
    {
        for (int i = 0; i < sectionCount; i++)
        {
            idft(
                reAux, imAux, reBuffer, imBuffer, 
                cosBuffer, sinBuffer,
                i * dftThreshold, dftThreshold
            );
        }
    }

    private static void parallelFracIDFT(
        float[] reAux, float[] imAux,
        float[] reBuffer, float[] imBuffer,
        float[] cosBuffer, float[] sinBuffer,
        int sectionCount)
    {
        Parallel.For(0, sectionCount, i =>
        {
            idft(
                reAux, imAux, reBuffer, imBuffer, 
                cosBuffer, sinBuffer,
                i * dftThreshold, dftThreshold
            );
        });
    }

    private static unsafe void dft(
        float[] re, float[] im,
        float[] oRe, float[] oIm,
        float[] cosBuffer, float[] sinBuffer,
        int offset, int N
    )
    {
        if (AdvSimd.IsSupported)
            smidDFT(re, im, oRe, oIm, cosBuffer, sinBuffer, offset, N);
        else if (Sse42.IsSupported)
            sse42DFT(re, im, oRe, oIm, cosBuffer, sinBuffer, offset, N);
        else if (Sse41.IsSupported)
            sse41DFT(re, im, oRe, oIm, cosBuffer, sinBuffer, offset, N);
        else if (Avx2.IsSupported)
            avxDFT(re, im, oRe, oIm, cosBuffer, sinBuffer, offset, N);
        else if (Sse3.IsSupported)
            sse3DFT(re, im, oRe, oIm, cosBuffer, sinBuffer, offset, N);
        else
            slowDFT(re, im, oRe, oIm, cosBuffer, sinBuffer, offset, N);
    }

    private static unsafe void idft(
        float[] re, float[] im,
        float[] oRe, float[] oIm,
        float[] cosBuffer, float[] sinBuffer,
        int offset, int N
    )
    {
        if (AdvSimd.IsSupported)
            smidIDFT(re, im, oRe, oIm, cosBuffer, sinBuffer, offset, N);
        else if (Sse42.IsSupported)
            sse42IDFT(re, im, oRe, oIm, cosBuffer, sinBuffer, offset, N);
        else if (Sse41.IsSupported)
            sse41IDFT(re, im, oRe, oIm, cosBuffer, sinBuffer, offset, N);
        else if (Avx2.IsSupported)
            avxIDFT(re, im, oRe, oIm, cosBuffer, sinBuffer, offset, N);
        else if (Sse3.IsSupported)
            sse3IDFT(re, im, oRe, oIm, cosBuffer, sinBuffer, offset, N);
        else
            slowIDFT(re, im, oRe, oIm, cosBuffer, sinBuffer, offset, N);
    }

    private static unsafe void slowDFT(
        float[] re, float[] im,
        float[] oRe, float[] oIm,
        float[] cosBuffer, float[] sinBuffer,
        int offset, int N
    )
    {
        fixed (float* 
            rep = re, imp = im, 
            orep = oRe, oimp = oIm,
            cosp = cosBuffer, sinp = sinBuffer
        )
        {
            float* tcosp = cosp, tsinp = sinp;
            float* torep = orep + offset, toimp = oimp + offset;
            float* endTorep = torep + N;

            for (; torep < endTorep; torep++, toimp++)
            {
                float reSum = 0f;
                float imSum = 0f;
                float* trep = rep + offset, timp = imp + offset;
                float* endTrep = trep + N;

                for (; trep < endTrep; trep++, timp++, tcosp++, tsinp++)
                {
                    var cos = *tcosp;
                    var sin = *tsinp;

                    var crrRe = *trep;
                    var crrIm = *timp;
                    reSum += crrRe * cos + crrIm * sin;
                    imSum += crrIm * cos - crrRe * sin;
                }
                *torep = reSum;
                *toimp = imSum;
            }
        }
    }

    private static unsafe void sse42DFT(
        float[] re, float[] im,
        float[] oRe, float[] oIm,
        float[] cosBuffer, float[] sinBuffer,
        int offset, int N
    )
    {
        fixed (float* 
            rep = re, imp = im, 
            orep = oRe, oimp = oIm,
            cosp = cosBuffer, sinp = sinBuffer
        )
        {
            float* tcosp = cosp, tsinp = sinp;
            float* torep = orep + offset, toimp = oimp + offset;
            float* endTorep = torep + N;
            float* sumPointer = stackalloc float[4];

            for (; torep < endTorep; torep++, toimp++)
            {
                float reSum = 0f;
                float imSum = 0f;
                float* trep = rep + offset, timp = imp + offset;
                float* endTrep = trep + N;

                for (; trep < endTrep; tcosp += 4, tsinp += 4, trep += 4, timp += 4)
                {
                    var cos = Sse42.LoadVector128(tcosp);
                    var sin = Sse42.LoadVector128(tsinp);
                    var rev = Sse42.LoadVector128(trep);
                    var imv = Sse42.LoadVector128(timp);

                    var m1 = Sse42.Multiply(cos, rev);
                    var m2 = Sse42.Multiply(sin, imv);
                    var m3 = Sse42.Add(m1, m2);

                    Sse42.Store(sumPointer, m3);
                    reSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];

                    m1 = Sse42.Multiply(imv, cos);
                    m2 = Sse42.Multiply(rev, sin);
                    m3 = Sse42.Subtract(m1, m2);

                    Sse42.Store(sumPointer, m3);
                    imSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];
                }
                *torep = reSum;
                *toimp = imSum;
            }
        }
    }

    private static unsafe void sse41DFT(
        float[] re, float[] im,
        float[] oRe, float[] oIm,
        float[] cosBuffer, float[] sinBuffer,
        int offset, int N
    )
    {
        fixed (float* 
            rep = re, imp = im, 
            orep = oRe, oimp = oIm,
            cosp = cosBuffer, sinp = sinBuffer
        )
        {
            float* tcosp = cosp, tsinp = sinp;
            float* torep = orep + offset, toimp = oimp + offset;
            float* endTorep = torep + N;
            float* sumPointer = stackalloc float[4];

            for (; torep < endTorep; torep++, toimp++)
            {
                float reSum = 0f;
                float imSum = 0f;
                float* trep = rep + offset, timp = imp + offset;
                float* endTrep = trep + N;

                for (; trep < endTrep; tcosp += 4, tsinp += 4, trep += 4, timp += 4)
                {
                    var cos = Sse41.LoadVector128(tcosp);
                    var sin = Sse41.LoadVector128(tsinp);
                    var rev = Sse41.LoadVector128(trep);
                    var imv = Sse41.LoadVector128(timp);

                    var m1 = Sse41.Multiply(cos, rev);
                    var m2 = Sse41.Multiply(sin, imv);
                    var m3 = Sse41.Add(m1, m2);

                    Sse41.Store(sumPointer, m3);
                    reSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];

                    m1 = Sse41.Multiply(imv, cos);
                    m2 = Sse41.Multiply(rev, sin);
                    m3 = Sse41.Subtract(m1, m2);

                    Sse41.Store(sumPointer, m3);
                    imSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];
                }
                *torep = reSum;
                *toimp = imSum;
            }
        }
    }

    private static unsafe void sse3DFT(
        float[] re, float[] im,
        float[] oRe, float[] oIm,
        float[] cosBuffer, float[] sinBuffer,
        int offset, int N
    )
    {
        fixed (float* 
            rep = re, imp = im, 
            orep = oRe, oimp = oIm,
            cosp = cosBuffer, sinp = sinBuffer
        )
        {
            float* tcosp = cosp, tsinp = sinp;
            float* torep = orep + offset, toimp = oimp + offset;
            float* endTorep = torep + N;
            float* sumPointer = stackalloc float[4];

            for (; torep < endTorep; torep++, toimp++)
            {
                float reSum = 0f;
                float imSum = 0f;
                float* trep = rep + offset, timp = imp + offset;
                float* endTrep = trep + N;

                for (; trep < endTrep; tcosp += 4, tsinp += 4, trep += 4, timp += 4)
                {
                    var cos = Sse3.LoadVector128(tcosp);
                    var sin = Sse3.LoadVector128(tsinp);
                    var rev = Sse3.LoadVector128(trep);
                    var imv = Sse3.LoadVector128(timp);

                    var m1 = Sse3.Multiply(cos, rev);
                    var m2 = Sse3.Multiply(sin, imv);
                    var m3 = Sse3.Add(m1, m2);

                    Sse3.Store(sumPointer, m3);
                    reSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];

                    m1 = Sse3.Multiply(imv, cos);
                    m2 = Sse3.Multiply(rev, sin);
                    m3 = Sse3.Subtract(m1, m2);

                    Sse3.Store(sumPointer, m3);
                    imSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];
                }
                *torep = reSum;
                *toimp = imSum;
            }
        }
    }

    private static unsafe void avxDFT(
        float[] re, float[] im,
        float[] oRe, float[] oIm,
        float[] cosBuffer, float[] sinBuffer,
        int offset, int N
    )
    {
        fixed (float* 
            rep = re, imp = im, 
            orep = oRe, oimp = oIm,
            cosp = cosBuffer, sinp = sinBuffer
        )
        {
            float* tcosp = cosp, tsinp = sinp;
            float* torep = orep + offset, toimp = oimp + offset;
            float* endTorep = torep + N;
            float* sumPointer = stackalloc float[4];

            for (; torep < endTorep; torep++, toimp++)
            {
                float reSum = 0f;
                float imSum = 0f;
                float* trep = rep + offset, timp = imp + offset;
                float* endTrep = trep + N;

                for (; trep < endTrep; tcosp += 4, tsinp += 4, trep += 4, timp += 4)
                {
                    var cos = Avx2.LoadVector128(tcosp);
                    var sin = Avx2.LoadVector128(tsinp);
                    var rev = Avx2.LoadVector128(trep);
                    var imv = Avx2.LoadVector128(timp);

                    var m1 = Avx2.Multiply(cos, rev);
                    var m2 = Avx2.Multiply(sin, imv);
                    var m3 = Avx2.Add(m1, m2);

                    Avx2.Store(sumPointer, m3);
                    reSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];

                    m1 = Avx2.Multiply(imv, cos);
                    m2 = Avx2.Multiply(rev, sin);
                    m3 = Avx2.Subtract(m1, m2);

                    Avx2.Store(sumPointer, m3);
                    imSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];
                }
                *torep = reSum;
                *toimp = imSum;
            }
        }
    }

    private static unsafe void smidDFT(
        float[] re, float[] im,
        float[] oRe, float[] oIm,
        float[] cosBuffer, float[] sinBuffer,
        int offset, int N
    )
    {
        fixed (float* 
            rep = re, imp = im, 
            orep = oRe, oimp = oIm,
            cosp = cosBuffer, sinp = sinBuffer
        )
        {
            float* tcosp = cosp, tsinp = sinp;
            float* torep = orep + offset, toimp = oimp + offset;
            float* endTorep = torep + N;
            float* sumPointer = stackalloc float[4];

            for (; torep < endTorep; torep++, toimp++)
            {
                float reSum = 0f;
                float imSum = 0f;
                float* trep = rep + offset, timp = imp + offset;
                float* endTrep = trep + N;

                for (; trep < endTrep; tcosp += 4, tsinp += 4, trep += 4, timp += 4)
                {
                    var cos = AdvSimd.LoadVector128(tcosp);
                    var sin = AdvSimd.LoadVector128(tsinp);
                    var rev = AdvSimd.LoadVector128(trep);
                    var imv = AdvSimd.LoadVector128(timp);

                    var m1 = AdvSimd.Multiply(cos, rev);
                    var m2 = AdvSimd.Multiply(sin, imv);
                    var m3 = AdvSimd.Add(m1, m2);

                    AdvSimd.Store(sumPointer, m3);
                    reSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];

                    m1 = AdvSimd.Multiply(imv, cos);
                    m2 = AdvSimd.Multiply(rev, sin);
                    m3 = AdvSimd.Subtract(m1, m2);

                    AdvSimd.Store(sumPointer, m3);
                    imSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];
                }
                *torep = reSum;
                *toimp = imSum;
            }
        }
    }

    private static unsafe void slowIDFT(
        float[] re, float[] im,
        float[] oRe, float[] oIm,
        float[] cosBuffer, float[] sinBuffer,
        int offset, int N
    )
    {
        fixed (float* 
            rep = re, imp = im, 
            orep = oRe, oimp = oIm,
            cosp = cosBuffer, sinp = sinBuffer
        )
        {
            float* tcosp = cosp, tsinp = sinp;
            float* torep = orep + offset, toimp = oimp + offset;
            float* endTorep = torep + N;

            for (; torep < endTorep; torep++, toimp++)
            {
                float reSum = 0f;
                float imSum = 0f;
                float* trep = rep + offset, timp = imp + offset;
                float* endTrep = trep + N;

                for (; trep < endTrep; trep++, timp++, tcosp++, tsinp++)
                {
                    var cos = *tcosp;
                    var sin = *tsinp;

                    var crrRe = *trep;
                    var crrIm = *timp;
                    reSum += crrRe * cos - crrIm * sin;
                    imSum += crrIm * cos + crrRe * sin;
                }
                *torep = reSum;
                *toimp = imSum;
            }
        }
    }

    private static unsafe void sse42IDFT(
        float[] re, float[] im,
        float[] oRe, float[] oIm,
        float[] cosBuffer, float[] sinBuffer,
        int offset, int N
    )
    {
        fixed (float* 
            rep = re, imp = im, 
            orep = oRe, oimp = oIm,
            cosp = cosBuffer, sinp = sinBuffer
        )
        {
            float* tcosp = cosp, tsinp = sinp;
            float* torep = orep + offset, toimp = oimp + offset;
            float* endTorep = torep + N;
            float* sumPointer = stackalloc float[4];

            for (; torep < endTorep; torep++, toimp++)
            {
                float reSum = 0f;
                float imSum = 0f;
                float* trep = rep + offset, timp = imp + offset;
                float* endTrep = trep + N;

                for (; trep < endTrep; tcosp += 4, tsinp += 4, trep += 4, timp += 4)
                {
                    var cos = Sse42.LoadVector128(tcosp);
                    var sin = Sse42.LoadVector128(tsinp);
                    var rev = Sse42.LoadVector128(trep);
                    var imv = Sse42.LoadVector128(timp);

                    var m1 = Sse42.Multiply(cos, rev);
                    var m2 = Sse42.Multiply(sin, imv);
                    var m3 = Sse42.Subtract(m1, m2);

                    Sse42.Store(sumPointer, m3);
                    reSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];

                    m1 = Sse42.Multiply(imv, cos);
                    m2 = Sse42.Multiply(rev, sin);
                    m3 = Sse42.Add(m1, m2);

                    Sse42.Store(sumPointer, m3);
                    imSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];
                }
                *torep = reSum;
                *toimp = imSum;
            }
        }
    }

    private static unsafe void sse41IDFT(
        float[] re, float[] im,
        float[] oRe, float[] oIm,
        float[] cosBuffer, float[] sinBuffer,
        int offset, int N
    )
    {
        fixed (float* 
            rep = re, imp = im, 
            orep = oRe, oimp = oIm,
            cosp = cosBuffer, sinp = sinBuffer
        )
        {
            float* tcosp = cosp, tsinp = sinp;
            float* torep = orep + offset, toimp = oimp + offset;
            float* endTorep = torep + N;
            float* sumPointer = stackalloc float[4];

            for (; torep < endTorep; torep++, toimp++)
            {
                float reSum = 0f;
                float imSum = 0f;
                float* trep = rep + offset, timp = imp + offset;
                float* endTrep = trep + N;

                for (; trep < endTrep; tcosp += 4, tsinp += 4, trep += 4, timp += 4)
                {
                    var cos = Sse41.LoadVector128(tcosp);
                    var sin = Sse41.LoadVector128(tsinp);
                    var rev = Sse41.LoadVector128(trep);
                    var imv = Sse41.LoadVector128(timp);

                    var m1 = Sse41.Multiply(cos, rev);
                    var m2 = Sse41.Multiply(sin, imv);
                    var m3 = Sse41.Subtract(m1, m2);

                    Sse41.Store(sumPointer, m3);
                    reSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];

                    m1 = Sse41.Multiply(imv, cos);
                    m2 = Sse41.Multiply(rev, sin);
                    m3 = Sse41.Add(m1, m2);

                    Sse41.Store(sumPointer, m3);
                    imSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];
                }
                *torep = reSum;
                *toimp = imSum;
            }
        }
    }

    private static unsafe void sse3IDFT(
        float[] re, float[] im,
        float[] oRe, float[] oIm,
        float[] cosBuffer, float[] sinBuffer,
        int offset, int N
    )
    {
        fixed (float* 
            rep = re, imp = im, 
            orep = oRe, oimp = oIm,
            cosp = cosBuffer, sinp = sinBuffer
        )
        {
            float* tcosp = cosp, tsinp = sinp;
            float* torep = orep + offset, toimp = oimp + offset;
            float* endTorep = torep + N;
            float* sumPointer = stackalloc float[4];

            for (; torep < endTorep; torep++, toimp++)
            {
                float reSum = 0f;
                float imSum = 0f;
                float* trep = rep + offset, timp = imp + offset;
                float* endTrep = trep + N;

                for (; trep < endTrep; tcosp += 4, tsinp += 4, trep += 4, timp += 4)
                {
                    var cos = Sse3.LoadVector128(tcosp);
                    var sin = Sse3.LoadVector128(tsinp);
                    var rev = Sse3.LoadVector128(trep);
                    var imv = Sse3.LoadVector128(timp);

                    var m1 = Sse3.Multiply(cos, rev);
                    var m2 = Sse3.Multiply(sin, imv);
                    var m3 = Sse3.Subtract(m1, m2);

                    Sse3.Store(sumPointer, m3);
                    reSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];

                    m1 = Sse3.Multiply(imv, cos);
                    m2 = Sse3.Multiply(rev, sin);
                    m3 = Sse3.Add(m1, m2);

                    Sse3.Store(sumPointer, m3);
                    imSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];
                }
                *torep = reSum;
                *toimp = imSum;
            }
        }
    }

    private static unsafe void avxIDFT(
        float[] re, float[] im,
        float[] oRe, float[] oIm,
        float[] cosBuffer, float[] sinBuffer,
        int offset, int N
    )
    {
        fixed (float* 
            rep = re, imp = im, 
            orep = oRe, oimp = oIm,
            cosp = cosBuffer, sinp = sinBuffer
        )
        {
            float* tcosp = cosp, tsinp = sinp;
            float* torep = orep + offset, toimp = oimp + offset;
            float* endTorep = torep + N;
            float* sumPointer = stackalloc float[4];

            for (; torep < endTorep; torep++, toimp++)
            {
                float reSum = 0f;
                float imSum = 0f;
                float* trep = rep + offset, timp = imp + offset;
                float* endTrep = trep + N;

                for (; trep < endTrep; tcosp += 4, tsinp += 4, trep += 4, timp += 4)
                {
                    var cos = Avx2.LoadVector128(tcosp);
                    var sin = Avx2.LoadVector128(tsinp);
                    var rev = Avx2.LoadVector128(trep);
                    var imv = Avx2.LoadVector128(timp);

                    var m1 = Avx2.Multiply(cos, rev);
                    var m2 = Avx2.Multiply(sin, imv);
                    var m3 = Avx2.Subtract(m1, m2);

                    Avx2.Store(sumPointer, m3);
                    reSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];

                    m1 = Avx2.Multiply(imv, cos);
                    m2 = Avx2.Multiply(rev, sin);
                    m3 = Avx2.Add(m1, m2);

                    Avx2.Store(sumPointer, m3);
                    imSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];
                }
                *torep = reSum;
                *toimp = imSum;
            }
        }
    }

    private static unsafe void smidIDFT(
        float[] re, float[] im,
        float[] oRe, float[] oIm,
        float[] cosBuffer, float[] sinBuffer,
        int offset, int N
    )
    {
        fixed (float* 
            rep = re, imp = im, 
            orep = oRe, oimp = oIm,
            cosp = cosBuffer, sinp = sinBuffer
        )
        {
            float* tcosp = cosp, tsinp = sinp;
            float* torep = orep + offset, toimp = oimp + offset;
            float* endTorep = torep + N;
            float* sumPointer = stackalloc float[4];

            for (; torep < endTorep; torep++, toimp++)
            {
                float reSum = 0f;
                float imSum = 0f;
                float* trep = rep + offset, timp = imp + offset;
                float* endTrep = trep + N;

                for (; trep < endTrep; tcosp += 4, tsinp += 4, trep += 4, timp += 4)
                {
                    var cos = AdvSimd.LoadVector128(tcosp);
                    var sin = AdvSimd.LoadVector128(tsinp);
                    var rev = AdvSimd.LoadVector128(trep);
                    var imv = AdvSimd.LoadVector128(timp);

                    var m1 = AdvSimd.Multiply(cos, rev);
                    var m2 = AdvSimd.Multiply(sin, imv);
                    var m3 = AdvSimd.Subtract(m1, m2);

                    AdvSimd.Store(sumPointer, m3);
                    reSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];

                    m1 = AdvSimd.Multiply(imv, cos);
                    m2 = AdvSimd.Multiply(rev, sin);
                    m3 = AdvSimd.Add(m1, m2);

                    AdvSimd.Store(sumPointer, m3);
                    imSum += sumPointer[3] + sumPointer[2] + sumPointer[1] + sumPointer[0];
                }
                *torep = reSum;
                *toimp = imSum;
            }
        }
    }

    private static unsafe void mergeDFTresults(
        int sectionCount, int div,
        float[] reBuffer, float[] imBuffer,
        float[] reAux, float[] imAux
    )
    {
        int swapCount = 0;
        float[] temp;
        float cos, sin;

        fixed (float* 
            reBufPointer = reBuffer, 
            reAuxPointer = reAux, 
            imBufPointer = imBuffer, 
            imAuxPointer = imAux
        )
        {
            float* rbp = reBufPointer;
            float* rap = reAuxPointer;
            float* ibp = reBufPointer;
            float* iap = imAuxPointer;
            while (sectionCount > 1)
            {
                for (int s = 0; s < sectionCount; s += 2)
                {
                    int start = div * s;
                    int end = start + div;
                    for (int i = start, j = end, k = 0; i < end; i++, j++, k++)
                    {
                        var param = MathF.Tau * k / (2 * div);
                        trigo(param, out cos, out sin);

                        float rbpj = *(rbp + j);
                        float ibpj = *(ibp + j);
                        float rbpi = *(rbp + i);
                        float ibpi = *(rbp + i);

                        float W = rbpj * cos + ibpj * sin;
                        *(rap + i) = rbpi + W;
                        *(rap + j) = rbpi - W;

                        W = ibpj * cos - rbpj * sin;
                        *(iap + i) = ibpi + W;
                        *(iap + j) = ibpi - W;
                    }
                }

                div *= 2;
                sectionCount /= 2;
                swapCount++;

                temp = reBuffer;
                reBuffer = reAux;
                reAux = temp;
                
                temp = imBuffer;
                imBuffer = imAux;
                imAux = temp;
            }
        }

        if (swapCount % 2 == 1)
            swapSignals(reBuffer, reAux, imBuffer, imAux);
    }
    
    private static void mergeIDFTresults(
        int sectionCount, int div,
        float[] reBuffer, float[] imBuffer,
        float[] reAux, float[] imAux
    )
    {
        int swapCount = 0;
        float[] temp;
        while (sectionCount > 1)
        {
            for (int s = 0; s < sectionCount; s += 2)
            {
                int start = div * s;
                int end = start + div;
                for (int i = start, j = end, k = 0; i < end; i++, j++, k++)
                {
                    var param = MathF.Tau * k / (2 * div);
                    var cos = MathF.Cos(param);
                    var sin = MathF.Sqrt(1 - cos * cos);

                    float W = reBuffer[j] * cos - imBuffer[j] * sin;
                    reAux[i] = reBuffer[i] + W;
                    reAux[j] = reBuffer[i] - W;

                    W = imBuffer[j] * cos + reBuffer[j] * sin;
                    imAux[i] = imBuffer[i] + W;
                    imAux[j] = imBuffer[i] - W;
                }
            }

            div *= 2;
            sectionCount /= 2;
            swapCount++;

            temp = reBuffer;
            reBuffer = reAux;
            reAux = temp;
            
            temp = imBuffer;
            imBuffer = imAux;
            imAux = temp;
        }
        
        if (swapCount % 2 == 1)
            swapSignals(reBuffer, reAux, imBuffer, imAux);
    }

    private static void swapSignals(
        float[] reSource, float[] reTarget,
        float[] imSource, float[] imTarget
    )
    {
        copySignal(reSource, reTarget);
        copySignal(imSource, imTarget);
    }

    private static void copySignal(float[] source, float[] target)
        => Buffer.BlockCopy(source, 0, target, 0, 4 * target.Length);

    private static int[] getFFTCoefs(int N, int div)
    {
        int size = N / div;
        int[] coefs = new int[size];
        int[] buffer = new int[size];
        for (int i = 0; i < size; i++)
            coefs[i] = i;
        
        recursiveEvenOddSplit(coefs, buffer, 0, size);
        return coefs;
    }

    private static void recursiveEvenOddSplit(int[] input, int[] output, int offset, int size)
    {
        if (size == 1)
            return;
        
        evenOddSplit(input, output, offset, size);
        recursiveEvenOddSplit(input, output, offset, size / 2);
        recursiveEvenOddSplit(input, output, offset + size / 2, size / 2);
    }

    private static void evenOddSplit(int[] data, int[] buff, int offset, int size)
    {
        int end = offset + size;
        for (int i = offset, j = offset, k = offset + size / 2; i < end; i += 2, j++, k++)
        {
            buff[j] = data[i];
            buff[k] = data[i + 1];
        }

        end = offset + size;
        for (int i = offset; i < end; i++)
            data[i] = buff[i];
    }

    private static float[] getCosBuffer(int N)
    {   
        if (cosBuffer != null)
            return cosBuffer;
        
        var cosArr = new float[N * N];

        for (int j = 0; j < N; j++)
        {
            for (int i = 0; i < N; i++)
                cosArr[i + j * N] = MathF.Cos(MathF.Tau * i * j / N);
        }
        
        cosBuffer = cosArr;
        return cosArr;
    }

    private static float[] getSinBuffer(int N)
    {
        if (sinBuffer != null)
            return sinBuffer;
        
        var sinArr = new float[N * N];

        for (int j = 0; j < N; j++)
        {
            for (int i = 0; i < N; i++)
                sinArr[i + j * N] = MathF.Sin(MathF.Tau * i * j / N);
        }

        sinBuffer = sinArr;
        return sinArr;
    }

    private static void generateSamples()
    {
        if (sinSamples is not null)
            return;
        
        sinSamples = new float[sampleSize];
        cosSamples = new float[sampleSize];

        for (int i = 0; i < sampleSize; i++)
        {
            var cos = MathF.Cos(MathF.Tau * (i - halfSampleSize) / halfSampleSize);
            cosSamples[i] = cos;
            sinSamples[i] = MathF.Sqrt(1 - cos * cos);
        }
    }

    private static void trigo(float x, out float sin, out float cos)
    {
        float fraction = x / MathF.Tau;
        int index = (int)(halfSampleSize * fraction);
        index %= halfSampleSize;
        index += halfSampleSize;
        sin = sinSamples[index];
        cos = cosSamples[index];
    }
}