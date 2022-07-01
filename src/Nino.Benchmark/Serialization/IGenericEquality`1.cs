// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nino.Benchmark
{
    public interface IGenericEquality<in T>
    {
        bool Equals(T obj);

        bool EqualsDynamic(dynamic obj);
    }
}
