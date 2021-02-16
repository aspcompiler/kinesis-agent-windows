/*
 * Copyright 2018 Amazon.com, Inc. or its affiliates. All Rights Reserved.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 * 
 *  http://aws.amazon.com/apache2.0
 * 
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */
using System;
using System.Collections.Generic;
using System.Text;

namespace Amazon.KinesisTap.Core
{
    public class FactoryCatalog<T> : IFactoryCatalog<T>
    {
        protected IDictionary<string, IFactory<T>> _catalog = new Dictionary<string, IFactory<T>>(StringComparer.OrdinalIgnoreCase);

        public IFactory<T> GetFactory(string entry)
        {
            if (!string.IsNullOrWhiteSpace(entry) && _catalog.TryGetValue(entry, out var factory))
            {
                return factory;
            }
            else
            {
                return null;
            }
        }

        public void RegisterFactory(string entry, IFactory<T> factory)
        {
            _catalog[entry] = factory;
        }
    }
}
