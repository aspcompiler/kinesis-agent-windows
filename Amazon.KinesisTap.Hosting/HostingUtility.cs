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
using Amazon.KinesisTap.Core;
using System.IO;

namespace Amazon.KinesisTap.Hosting
{
    /// <summary>
    /// Defines numerous constants and helper methods related to hosting KinesisTap
    /// </summary>
    public static class HostingUtility
    {
        public const string DefaultConfigFileName = "appsettings.json";

        public const string NLogConfigFileName = "NLog.xml";

        /// <summary>
        /// The key used for storing the config file-id mapping in the parameter store.
        /// </summary>
        public const string PersistentConfigFileIdMapStoreKey = "ConfigFilePathIdMap";

        /// <summary>
        /// Key used for storing the path to the NLog config file.
        /// </summary>
        public const string NLogConfigPathKey = "DefaultNLogConfigPathKey";

        /// <summary>
        /// The key used for storing configuration directory path.
        /// </summary>
        public const string ConfigDirPathKey = "ConfigDirPathKey";

        /// <summary>
        /// Store some KinesisTap conventional values to the parameter store.
        /// </summary>
        /// <param name="store">The <see cref="IParameterStore"/> instance.</param>
        /// <remarks>
        /// This is used to store conventional values to the parameter store when KT starts up.
        /// Any value set from the previous run of KT is overwritten. 
        /// Doing this provides different KT classes (SessionManager, LogManager, PersistentConfigFileIdMap etc.) a unified way to resolve values,
        /// and allows easier testing as well.
        /// </remarks>
        public static void StoreConventionalValues(this IParameterStore store)
        {
            store.SetParameter(ConfigDirPathKey,
               Path.Combine(Utility.GetKinesisTapConfigPath()));

            store.SetParameter(NLogConfigPathKey,
               Path.Combine(Utility.GetNLogConfigDirectory(), NLogConfigFileName));
        }

        public static string GetConfigDirPath(this IParameterStore store) => store.GetParameter(ConfigDirPathKey);

        public static string GetDefaultConfigFilePath(this IParameterStore store)
            => Path.Combine(store.GetParameter(ConfigDirPathKey), DefaultConfigFileName);

        public static string GetExtraConfigDirPath(this IParameterStore store)
            => Path.Combine(store.GetParameter(ConfigDirPathKey), Utility.ExtraConfigDirectoryName);
    }
}
