using MathNet.Numerics.LinearAlgebra;

namespace Ssvep.Speller.Core.Classification;

internal static class CcaMath
{
    public static double CanonicalCorrelation(double[,] x, double[,] y)
    {
        Matrix<double> xMatrix = Center(ToMatrix(x));
        Matrix<double> yMatrix = Center(ToMatrix(y));
        int sampleCount = xMatrix.RowCount;
        double denominator = Math.Max(1, sampleCount - 1);

        Matrix<double> sxx = xMatrix.TransposeThisAndMultiply(xMatrix) / denominator;
        Matrix<double> syy = yMatrix.TransposeThisAndMultiply(yMatrix) / denominator;
        Matrix<double> sxy = xMatrix.TransposeThisAndMultiply(yMatrix) / denominator;
        Matrix<double> syx = sxy.Transpose();

        AddRidge(sxx, 1e-8);
        AddRidge(syy, 1e-8);

        Matrix<double> transform = sxx.Inverse() * sxy * syy.Inverse() * syx;
        var evd = transform.Evd();
        double maxEigen = evd.EigenValues.Select(value => value.Real).DefaultIfEmpty(0.0).Max();
        if (double.IsNaN(maxEigen) || maxEigen < 0)
        {
            return 0.0;
        }

        return Math.Min(1.0, Math.Sqrt(maxEigen));
    }

    private static Matrix<double> ToMatrix(double[,] data)
    {
        return Matrix<double>.Build.DenseOfArray(data);
    }

    private static Matrix<double> Center(Matrix<double> matrix)
    {
        var centered = matrix.Clone();
        for (int column = 0; column < centered.ColumnCount; column++)
        {
            double mean = centered.Column(column).Average();
            for (int row = 0; row < centered.RowCount; row++)
            {
                centered[row, column] -= mean;
            }
        }

        return centered;
    }

    private static void AddRidge(Matrix<double> matrix, double ridge)
    {
        for (int i = 0; i < Math.Min(matrix.RowCount, matrix.ColumnCount); i++)
        {
            matrix[i, i] += ridge;
        }
    }
}
