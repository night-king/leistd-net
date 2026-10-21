using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Leistd.ExceptionHandling.AspNetCore.Validation;

// 让 IValidatableObject.Validate 的错误文案与特性校验同样本地化：经宿主的 DataAnnotations 本地化器，以文案为资源键。
// MVC 的 DataAnnotations 提供器为每个 IValidatableObject 类型追加一个内置校验器，它不本地化文案。
// 本提供器排在它之后，把那一项替换为同语义、补上本地化的校验器；宿主移除了 DataAnnotations 提供器
// （不做 IValidatableObject 校验）时不追加任何校验器。成员名照内置行为原样记录，字段名由 ValidationFieldNames 在写出时统一换算。
internal sealed class ValidatableObjectModelValidatorProvider(
    IOptions<MvcDataAnnotationsLocalizationOptions> localizationOptions,
    IStringLocalizerFactory? stringLocalizerFactory) : IMetadataBasedModelValidatorProvider
{
    // 内置校验器不对应任何特性（ValidatorMetadata 为空），且出自 DataAnnotations 程序集；
    // 该程序集为一个模型只追加这一个无元数据的校验器。按程序集识别，不依赖内部类型名。
    private static readonly Assembly DataAnnotationsAssembly = typeof(MvcDataAnnotationsLocalizationOptions).Assembly;

    public void CreateValidators(ModelValidatorProviderContext context)
    {
        var metadata = context.ModelMetadata;
        if (!typeof(IValidatableObject).IsAssignableFrom(metadata.ModelType))
            return;

        var results = context.Results;
        for (var i = 0; i < results.Count; i++)
        {
            var item = results[i];
            if (item.ValidatorMetadata is null && item.Validator?.GetType().Assembly == DataAnnotationsAssembly)
            {
                results[i] = new ValidatorItem
                {
                    Validator = new ValidatableObjectModelValidator(CreateLocalizer(metadata.ModelType)),
                    IsReusable = true
                };
            }
        }
    }

    // 只替换已有的校验器，从不新增；是否需要校验由 DataAnnotations 提供器判定
    public bool HasValidators(Type modelType, IList<object> validatorMetadata) => false;

    // 与 MVC 为特性校验取本地化器的条件一致：宿主启用了 DataAnnotations 本地化（有工厂且设了提供委托）才本地化。
    // 文案出自该 DTO 类型自己的 Validate，按模型类型取本地化器（MVC 为特性取的是声明特性的容器类型）。
    private IStringLocalizer? CreateLocalizer(Type modelType) =>
        stringLocalizerFactory is not null && localizationOptions.Value.DataAnnotationLocalizerProvider is { } provider
            ? provider(modelType, stringLocalizerFactory)
            : null;

    private sealed class ValidatableObjectModelValidator(IStringLocalizer? localizer) : IModelValidator
    {
        public IEnumerable<ModelValidationResult> Validate(ModelValidationContext context)
        {
            if (context.Model is null)
                return [];
            if (context.Model is not IValidatableObject validatable)
            {
                throw new InvalidOperationException(
                    $"The model of type '{context.Model.GetType()}' does not implement {nameof(IValidatableObject)}.");
            }

            // 与 MVC 内置校验器构造同一个校验上下文
            var validationContext = new ValidationContext(
                validatable,
                context.ActionContext?.HttpContext?.RequestServices,
                items: null)
            {
                DisplayName = context.ModelMetadata.GetDisplayName(),
                MemberName = context.ModelMetadata.Name
            };

            var results = new List<ModelValidationResult>();
            foreach (var result in validatable.Validate(validationContext))
            {
                if (result == ValidationResult.Success)
                    continue;

                var message = Localize(result.ErrorMessage);
                // 成员名与内置校验器一样原样记录（含没有成员名的模型级错误），字段名由自动 400 统一换算，见 ValidationFieldNames
                var memberNames = result.MemberNames.ToList();
                if (memberNames.Count == 0)
                {
                    results.Add(new ModelValidationResult(memberName: null, message));
                    continue;
                }

                results.AddRange(memberNames.Select(memberName => new ModelValidationResult(memberName, message)));
            }

            return results;
        }

        // 与特性校验相同：文案本身作资源键，未命中时本地化器返回原文
        private string? Localize(string? message) =>
            localizer is null || string.IsNullOrEmpty(message) ? message : localizer[message].Value;
    }
}
