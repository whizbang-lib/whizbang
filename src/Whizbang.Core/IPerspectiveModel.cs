namespace Whizbang.Core;

public interface IPerspectiveModel<out TModel> where TModel : class
{
    TModel CurrentData();
}
