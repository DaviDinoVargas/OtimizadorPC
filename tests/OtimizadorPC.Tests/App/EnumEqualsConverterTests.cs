using System.Globalization;
using System.Windows.Data;
using OtimizadorPC.App.Converters;
using OtimizadorPC.Core.Settings;

namespace OtimizadorPC.Tests.App;

/// <summary>Os botões de opção (pílulas) dependem deste conversor nos dois sentidos.</summary>
public sealed class EnumEqualsConverterTests
{
    private readonly EnumEqualsConverter _c = new();
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    [Theory]
    [InlineData(typeof(ThemeMode), "Claro", ThemeMode.Claro)]
    [InlineData(typeof(int), "15", 15)]
    [InlineData(typeof(int), "60", 60)]
    [InlineData(typeof(bool), "True", true)]
    [InlineData(typeof(bool), "False", false)]
    public void Marcar_a_opcao_devolve_o_valor_no_tipo_da_propriedade(Type type, string parameter, object expected)
    {
        Assert.Equal(expected, _c.ConvertBack(true, type, parameter, Pt));
    }

    [Fact]
    public void Desmarcar_nao_altera_nada()
    {
        Assert.Same(Binding.DoNothing, _c.ConvertBack(false, typeof(int), "15", Pt));
    }

    [Theory]
    [InlineData(30, "30", true)]
    [InlineData(30, "15", false)]
    [InlineData(true, "True", true)]
    public void Opcao_aparece_marcada_quando_o_valor_coincide(object value, string parameter, bool expected)
    {
        Assert.Equal(expected, _c.Convert(value, typeof(bool), parameter, Pt));
    }
}
