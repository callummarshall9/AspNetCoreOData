using System;
using System.Text.Json;
using Microsoft.AspNetCore.OData.Query.Wrapper;
using Microsoft.AspNetCore.OData.Results;

namespace Microsoft.AspNetCore.OData
{
    public static class ODataJsonSerializerOptionsExtensions
    {
        public static JsonSerializerOptions GetSerializerOptions() {
            JsonSerializerOptions serializerOptions = new JsonSerializerOptions();

            serializerOptions.Converters.Add(new SelectExpandWrapperConverter());
            serializerOptions.Converters.Add(new PageResultValueConverter());
            serializerOptions.Converters.Add(new DynamicTypeWrapperConverter());
            serializerOptions.Converters.Add(new SingleResultValueConverter());

            return serializerOptions;
        }
    }
}