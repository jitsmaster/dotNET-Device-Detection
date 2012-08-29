﻿/* *********************************************************************
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0.
 * 
 * If a copy of the MPL was not distributed with this file, You can obtain
 * one at http://mozilla.org/MPL/2.0/.
 * 
 * This Source Code Form is “Incompatible With Secondary Licenses”, as
 * defined by the Mozilla Public License, v. 2.0.
 * ********************************************************************* */

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using FiftyOne.Foundation.Mobile.Detection.Handlers;

namespace FiftyOne.Foundation.Mobile.Detection.Binary
{
    /// <summary>
    /// Used to read device data into the detection provider.
    /// </summary>
    public class Reader
    {
        #region Static Public Methods

        /// <summary>
        /// Creates a new provider from the binary file supplied.
        /// </summary>
        /// <param name="file">Binary file to use to create the provider.</param>
        /// <returns>A new provider initialised with data from the file provided.</returns>
        public static Provider Create(string file)
        {
            FileInfo fileInfo = new FileInfo(file);
            if (fileInfo.Exists)
            {
                using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    return Create(stream);
                }
            }
            return null;
        }

        /// <summary>
        /// Creates a new provider from the binary data array supplied.
        /// </summary>
        /// <param name="data">Binary data array supplied.</param>
        /// <returns>A new provider initialised with data from the stream provided.</returns>
        public static Provider Create(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream(data))
                return Create(ms);
        }

        /// <summary>
        /// Creates a new provider from the binary stream supplied.
        /// </summary>
        /// <param name="stream">Binary stream to use to create the provider.</param>
        /// <returns>A new provider initialised with data from the stream provided.</returns>
        public static Provider Create(Stream stream)
        {
            Provider provider = new Provider();
            using (BinaryReader reader = new BinaryReader(new GZipStream(stream, CompressionMode.Decompress)))
            {
                Add(provider, reader);
            }
            return provider;
        }

        #endregion

        #region Static Private Methods

        /// <summary>
        /// Adds the data from the binary reader to the provider.
        /// </summary>
        /// <param name="provider">The provider to have data added to/</param>
        /// <param name="reader">Reader connected to the input stream.</param>
        private static void Add(Provider provider, BinaryReader reader)
        {
            // Read and ignore the copyright notice. This is ignored.
            string copyright = reader.ReadString();

            // Ignore any additional information at the moment.
            reader.ReadString();

            // Check the versions are correct.
            Version streamFormat = ReadVersion(reader);
            if ((streamFormat.Major == BinaryConstants.FormatVersion.Major &&
                streamFormat.Minor == BinaryConstants.FormatVersion.Minor) == false)
                throw new BinaryException(String.Format(
                    "The data provided is not supported by this version of Foundation. " +
                    "The version provided is '{0}' and the version expected is '{1}'.",
                    streamFormat,
                    BinaryConstants.FormatVersion));

            // Load the data now that validation is completed.
            ReadStrings(reader, provider.Strings);
            ReadHandlers(reader, provider);
            ReadDevices(reader, provider, null);
            ReadPublishedDate(reader, provider);
            ReadManifest(reader, provider);
            ReadDataSetName(reader, provider);
        }

        /// <summary>
        /// Reads the devices and any children.
        /// </summary>
        /// <param name="reader">BinaryReader of the input stream.</param>
        /// <param name="provider">The provider the device will be added to.</param>
        /// <param name="parent">The parent of the device, or null if a root device.</param>
        private static void ReadDevices(BinaryReader reader, Provider provider, DeviceInfo parent)
        {
            short count = reader.ReadInt16();
            for (short i = 0; i < count; i++)
            {
                // Get the device id string.
                int uniqueDeviceIDStringIndex = reader.ReadInt32();
                string uniqueDeviceID =
                    uniqueDeviceIDStringIndex >= 0 ?
                    provider.Strings.Get(uniqueDeviceIDStringIndex) :
                    String.Empty;

                DeviceInfo device;

                // Get the number of useragents available for the device.
                short userAgentCount = reader.ReadInt16();
                               
                if (userAgentCount > 0)
                {
                    // Read the 1st one, if one is present to assign to the master device.
                    device = new DeviceInfo(
                        provider,
                        uniqueDeviceID,
                        reader.ReadInt32(),
                        parent);

                    // Add the device to the handlers.
                    foreach (short index in ReadDeviceHandlers(reader))
                        provider.Handlers[index].Set(device);

                    // Reduce the number of useragents by 1 because we've read
                    // the 1st one.
                    userAgentCount--;
                }
                else
                {
                    // Create the device and don't assign any useragents.
                    device = new DeviceInfo(
                        provider,
                        uniqueDeviceID,
                        parent);
                }

                // Create new devices as children of this one to hold the
                // remaining user agent strings.
                for (int u = 0; u < userAgentCount; u++)
                {
                    // Get the user agent string index and create a new
                    // device.
                    DeviceInfo uaDevice = new DeviceInfo(
                        provider,
                        uniqueDeviceID,
                        reader.ReadInt32(),
                        device);

                    // Add the device to the handlers.
                    foreach (short index in ReadDeviceHandlers(reader))
                        provider.Handlers[index].Set(uaDevice);
                }
                                
                // Add the device to the list of all devices.
                int hashCode = device.DeviceId.GetHashCode();
                if (provider.AllDevices.ContainsKey(hashCode))
                {
                    // Yes. Add this device to the list.
                    provider.AllDevices[hashCode].Add(device);
                }
                else
                {
                    // No. So add the new device.
                    provider.AllDevices.Add(hashCode, new List<BaseDeviceInfo>(new BaseDeviceInfo[] { device }));
                }

                // Get the remaining properties and values to the device.
                ReadCollection(reader, device.Properties);

                // Read the child devices.
                ReadDevices(reader, provider, device);
            }
        }

        /// <summary>
        /// Reads all the handler indexes that support this device.
        /// </summary>
        /// <param name="reader"></param>
        /// <returns></returns>
        private static List<short> ReadDeviceHandlers(BinaryReader reader)
        {
            List<short> indexes = new List<short>();
            short count = reader.ReadInt16();
            for (int i = 0; i < count; i++)
                indexes.Add(reader.ReadInt16());
            return indexes;
        }
        
        /// <summary>
        /// Reads the handler from the binary reader.
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="provider"></param>
        private static void ReadHandlers(BinaryReader reader, Provider provider)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                Handler handler = CreateHandler(reader, provider);
                ReadHandlerRegexes(reader, handler.CanHandleRegex);
                ReadHandlerRegexes(reader, handler.CantHandleRegex);
                provider.Handlers.Add(handler);
            }
        }

        /// <summary>
        /// Reads the regular expressions used to determine if the user agent can be handled
        /// by the handler.
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="list"></param>
        private static void ReadHandlerRegexes(BinaryReader reader, List<HandleRegex> list)
        {
            int count = reader.ReadInt16();
            for (int i = 0; i < count; i++)
            {
                string pattern = reader.ReadString();
                HandleRegex regex = new HandleRegex(pattern);
                ReadHandlerRegexes(reader, regex.Children);
                list.Add(regex);
            }
        }

        /// <summary>
        /// Reads a collection of properties.
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="properties"></param>
        private static void ReadCollection(BinaryReader reader, Collection properties)
        {
            // Read the number of properties.
            short propertiesCount = reader.ReadInt16();
            for (int p = 0; p < propertiesCount; p++)
            {
                // Get the index of the properties string.
                int propertyNameStringIndex = reader.ReadInt32();

                // Read the number of values.
                byte valuesCount = reader.ReadByte();
                
                // Read all the values.
                List<int> values = new List<int>();
                for (int v = 0; v < valuesCount; v++)
                    values.Add(reader.ReadInt32());

                // Add the property and values.
                properties.Add(propertyNameStringIndex, values);
            }
        }

        /// <summary>
        /// Creates the handler for the current provider.
        /// </summary>
        /// <param name="reader">Data source being processed.</param>
        /// <param name="provider">Provider the new handler should be assigned to.</param>
        /// <returns>An instance of the new handler.</returns>
        private static Handler CreateHandler(BinaryReader reader, Provider provider)
        {
            HandlerTypes type = (HandlerTypes)reader.ReadByte();
            byte confidence = reader.ReadByte();
            string name = reader.ReadString();
            bool checkUAProfs = reader.ReadBoolean();
            string defaultDeviceId = String.Empty;

            switch (type)
            {
                case HandlerTypes.EditDistance:
                    return new Handlers.EditDistanceHandler(
                        provider, name, defaultDeviceId, confidence, checkUAProfs);
                case HandlerTypes.ReducedInitialString:
                    return new Handlers.ReducedInitialStringHandler(
                        provider, name, defaultDeviceId, confidence, checkUAProfs, reader.ReadString());
                case HandlerTypes.RegexSegment:
                    Handlers.RegexSegmentHandler handler = new Handlers.RegexSegmentHandler(
                        provider, name, defaultDeviceId, confidence, checkUAProfs);
                    ReadRegexSegmentHandler(reader, handler);
                    return handler;
            }

            throw new BinaryException(String.Format("Type '{0}' is not a recognised handler.", type));
        }

        /// <summary>
        /// Reads the regular expressions and weights that form the handler.
        /// </summary>
        /// <param name="reader">Data source being processed.</param>
        /// <param name="handler">Handler being created.</param>
        private static void ReadRegexSegmentHandler(BinaryReader reader, RegexSegmentHandler handler)
        {
            int count = reader.ReadInt16();
            for (int i = 0; i < count; i++)
            {
                handler.Segments.Add(new RegexSegmentHandler.RegexSegment(
                    reader.ReadString(), reader.ReadInt32()));
            }
        }

        /// <summary>
        /// Reads the initial number of strings into the strings collection.
        /// </summary>
        /// <param name="reader">Data source being processed.</param>
        /// <param name="strings">Strings instance to be added to.</param>
        private static void ReadStrings(BinaryReader reader, Strings strings)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                short length = reader.ReadInt16();
                byte[] value = reader.ReadBytes(length);
                strings.Add(Encoding.Unicode.GetString(
                    Encoding.Convert(Encoding.ASCII, Encoding.Unicode, value)));
            }
        }

        /// <summary>
        /// Returns the version number from the stream. The version number represents
        /// the data format used in the file.
        /// </summary>
        /// <param name="reader">Reader connected to an input stream.</param>
        /// <returns>The version number read from the stream.</returns>
        private static Version ReadVersion(BinaryReader reader)
        {
            return new Version(
                reader.ReadInt32(),
                reader.ReadInt32());
        }

        /// <summary>
        /// Reads the name of the data set.
        /// </summary>
        /// <param name="reader">Data source being processed.</param>
        /// <param name="provider">Provider the new handler should be assigned to.</param>
        private static void ReadDataSetName(BinaryReader reader, Provider provider)
        {
            try
            {
                provider._dataSetName = ReadString(reader);
            }
            catch (EndOfStreamException)
            {
                // Nothing we can do as data is not included.
                EventLog.Debug("EndOfStreamException reading data set name.");
                provider._dataSetName = "Unknown";
            }
        }

        /// <summary>
        /// Reads the date and time the file was published.
        /// </summary>
        /// <param name="reader">Data source being processed.</param>
        /// <param name="provider">Provider the new handler should be assigned to.</param>
        private static void ReadPublishedDate(BinaryReader reader, Provider provider)
        {
            try
            {
                provider._publishedDate = new DateTime(reader.ReadInt64());
            }
            catch (EndOfStreamException)
            {
                // Nothing we can do as data is not included.
                EventLog.Debug("EndOfStreamException reading published date.");
                provider._publishedDate = DateTime.MinValue;
            }
        }
        
        /// <summary>
        /// Adds manifest information to the provider if the data file includes it.
        /// </summary>
        /// <param name="reader">Data source being processed.</param>
        /// <param name="provider">Provider the new handler should be assigned to.</param>
        private static void ReadManifest(BinaryReader reader, Provider provider)
        {
            // Ensure any old properties are removed.
            provider.Properties.Clear();

            try
            {
                int countOfProperties = reader.ReadInt32();
                for (int p = 0; p < countOfProperties; p++)
                {
                    // Create the property.
                    Property property = new Property(
                        provider,
                        provider.Strings.Get(reader.ReadInt32()),
                        ReadString(reader),
                        ReadString(reader),
                        reader.ReadBoolean(),
                        reader.ReadBoolean(),
                        reader.ReadBoolean());

                    // Add the values to the list.
                    int countOfValues = reader.ReadInt32();
                    for (int v = 0; v < countOfValues; v++)
                    {
                        Value value = new Value(
                            property,
                            provider.Strings.Get(reader.ReadInt32()),
                            ReadString(reader),
                            ReadString(reader));
                        property.Values.Add(value);
                    }

                    // Finally add the property to the list of properties.
                    provider.Properties.Add(property);
                }
            }
            catch (EndOfStreamException)
            {
                // Nothing we can do. Clear the data.
                EventLog.Debug("EndOfStreamException reading manifest.");
                provider.Properties.Clear();
            }
        }

        /// <summary>
        /// Checks for a boolean value to indicate if the string is present.
        /// If it is present then read the string and return it.
        /// </summary>
        /// <param name="reader"></param>
        /// <returns></returns>
        private static string ReadString(BinaryReader reader)
        {
            bool isPresent = reader.ReadBoolean();
            if (isPresent)
                return reader.ReadString();
            return null;
        }

        #endregion
    }
}
