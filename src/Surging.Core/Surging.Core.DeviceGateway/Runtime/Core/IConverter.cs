﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Surging.Core.DeviceGateway.Runtime.Core
{
    public interface IConverter<T>
    {
        T Convert(object value);
    }

  
}
