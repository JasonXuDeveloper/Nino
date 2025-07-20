using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Filter.Operation;

public class Union : IFilter
{
    private readonly List<IFilter> _filters = new(64);

    public Union With(IFilter filter)
    {
        _filters.Add(filter);
        return this;
    }

    public Union With(IFilter filter1, IFilter filter2)
    {
        _filters.Add(filter1);
        _filters.Add(filter2);
        return this;
    }

    public Union With(IFilter filter1, IFilter filter2, IFilter filter3)
    {
        _filters.Add(filter1);
        _filters.Add(filter2);
        _filters.Add(filter3);
        return this;
    }

    public Union With(IFilter filter1, IFilter filter2, IFilter filter3, IFilter filter4)
    {
        _filters.Add(filter1);
        _filters.Add(filter2);
        _filters.Add(filter3);
        _filters.Add(filter4);
        return this;
    }

    public Union With(IFilter filter1, IFilter filter2, IFilter filter3, IFilter filter4, IFilter filter5)
    {
        _filters.Add(filter1);
        _filters.Add(filter2);
        _filters.Add(filter3);
        _filters.Add(filter4);
        _filters.Add(filter5);
        return this;
    }

    public Union With(IFilter filter1, IFilter filter2, IFilter filter3, IFilter filter4, IFilter filter5,
        IFilter filter6)
    {
        _filters.Add(filter1);
        _filters.Add(filter2);
        _filters.Add(filter3);
        _filters.Add(filter4);
        _filters.Add(filter5);
        _filters.Add(filter6);
        return this;
    }

    public Union With(IFilter filter1, IFilter filter2, IFilter filter3, IFilter filter4, IFilter filter5,
        IFilter filter6, IFilter filter7)
    {
        _filters.Add(filter1);
        _filters.Add(filter2);
        _filters.Add(filter3);
        _filters.Add(filter4);
        _filters.Add(filter5);
        _filters.Add(filter6);
        _filters.Add(filter7);
        return this;
    }
    
    public Union With(IFilter filter1, IFilter filter2, IFilter filter3, IFilter filter4, IFilter filter5,
        IFilter filter6, IFilter filter7, IFilter filter8)
    {
        _filters.Add(filter1);
        _filters.Add(filter2);
        _filters.Add(filter3);
        _filters.Add(filter4);
        _filters.Add(filter5);
        _filters.Add(filter6);
        _filters.Add(filter7);
        _filters.Add(filter8);
        return this;
    }
    
    public Union With(IFilter filter1, IFilter filter2, IFilter filter3, IFilter filter4, IFilter filter5,
        IFilter filter6, IFilter filter7, IFilter filter8, IFilter filter9)
    {
        _filters.Add(filter1);
        _filters.Add(filter2);
        _filters.Add(filter3);
        _filters.Add(filter4);
        _filters.Add(filter5);
        _filters.Add(filter6);
        _filters.Add(filter7);
        _filters.Add(filter8);
        _filters.Add(filter9);
        return this;
    }

    public Union With(params IFilter[] filter)
    {
        _filters.AddRange(filter);
        return this;
    }

    public bool Filter(ITypeSymbol symbol)
    {
        foreach (var filter in _filters)
        {
            if (filter.Filter(symbol))
                return true;
        }

        return false;
    }
}