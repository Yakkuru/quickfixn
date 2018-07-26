﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using QuickFix.Fields;

namespace QuickFix
{
    /// <summary>
    /// The default factory for creating FIX message instances.  (In the v2.0 release, this class should be made sealed.)
    /// </summary>
    public class DefaultMessageFactory : IMessageFactory
    {
        private static int _dllLoadFlag;

        /// <summary>
        /// key is BeginString (including the fake FIX50 beginstrings)
        /// </summary>
        private readonly IReadOnlyDictionary<string, IMessageFactory> _factories;

        private QuickFix.Fields.ApplVerID _defaultApplVerId;
        
        /// <summary>
        /// This constructor will
        /// 1. Dynamically load all QuickFix.*.dll assemblies into the current appdomain
        /// 2. Find all IMessageFactory implementations in these assemblies (must have parameterless constructor)
        /// 3. Use them based on begin strings they support
        /// </summary>
        /// <param name="defaultApplVerId">ApplVerID value used by default in Create methods that don't explicitly specify it (only relevant for FIX5+)</param>
        public DefaultMessageFactory(string defaultApplVerId = QuickFix.FixValues.ApplVerID.FIX50SP2)
        {
            _defaultApplVerId = new ApplVerID(defaultApplVerId);
            var assemblies = GetAppDomainAssemblies();
            var factories = GetMessageFactories(assemblies);
            _factories = ConvertToDictionary(factories);
        }

        /// <summary>
        /// This constructor will save the IMessageFactory instances based on what they return from GetSupportedBeginStrings()
        /// </summary>
        /// <param name="factories">IMessageFactory instances</param>
        [System.Obsolete("Nothing uses this, so no reason to keep it")]
        public DefaultMessageFactory(IEnumerable<IMessageFactory> factories)
        {
            _factories = ConvertToDictionary(factories);
        }

        /// <summary>
        /// This constructor will
        /// 1. Locate all IMessageFactory implementations from the provided assemblies (must have parameterless constructor)
        /// 2. Use them based on begin strings they support
        /// </summary>
        /// <param name="assemblies">Assemblies that may contain IMessageFactory implementations</param>
        public DefaultMessageFactory(IEnumerable<Assembly> assemblies)
        {
            var factories = GetMessageFactories(assemblies);
            _factories = ConvertToDictionary(factories);
        }

        #region IMessageFactory Members

        public ICollection<string> GetSupportedBeginStrings()
        {
            return _factories.Keys.ToList();
        }

        /*
         *     @Override
    public Message create(String beginString, ApplVerID applVerID, String msgType) {
        MessageFactory messageFactory = messageFactories.get(beginString);
        if (beginString.equals(BEGINSTRING_FIXT11) && !MessageUtils.isAdminMessage(msgType)) {
            if (applVerID == null) {
                applVerID = new ApplVerID(defaultApplVerID.getValue());
            }
            messageFactory = messageFactories.get(MessageUtils.toBeginString(applVerID));
        }

        if (messageFactory != null) {
            return messageFactory.create(beginString, applVerID, msgType);
        }

        Message message = new Message();
        message.getHeader().setString(MsgType.FIELD, msgType);

        return message;
    }
    */

        public Message Create(string beginString, string msgType)
        {
            return Create(beginString, _defaultApplVerId, msgType);
        }

        public Message Create(string beginString, QuickFix.Fields.ApplVerID applVerID, string msgType)
        {
            IMessageFactory messageFactory = _factories[beginString];

            if (beginString == QuickFix.Values.BeginString_FIXT11 && !Message.IsAdminMsgType(msgType))
            {
                if (applVerID == null)
                    applVerID = _defaultApplVerId;
                messageFactory = _factories[QuickFix.FixValues.ApplVerID.ToBeginString(applVerID.Obj)];
            }

            if (messageFactory != null)
                return messageFactory.Create(beginString, applVerID, msgType);

            var message = new Message();
            message.Header.SetField(new StringField(QuickFix.Fields.Tags.MsgType, msgType));
            return message;
        }

        public Group Create(string beginString, string msgType, int groupCounterTag)
        {
            // FIXME: This is a hack.  FIXT11 could mean 50 or 50sp1 or 50sp2.
            // We need some way to choose which 50 version it is.
            // Choosing 50 here is not adequate.
            var key = beginString.Equals(FixValues.BeginString.FIXT11)
                ? FixValues.BeginString.FIX50
                : beginString;

            if (_factories.TryGetValue(key, out var factory))
            {
                return factory.Create(beginString, msgType, groupCounterTag);
            }
            else
            {
                throw new UnsupportedVersion(beginString);
            }
        }

        #endregion

        #region Dynamic assembly load related methods

        private static Dictionary<string, IMessageFactory> ConvertToDictionary(IEnumerable<IMessageFactory> factories)
        {
            var dict = new Dictionary<string, IMessageFactory>();
            foreach (var factory in factories)
            {
                foreach (var beginString in factory.GetSupportedBeginStrings())
                {
                    dict[beginString] = factory;
                }
            }

            return dict;
        }

        private static void LoadLocalDlls()
        {
            const int @true = 1;

            // Because we want to attempt load assemblies once only
            var loadFlag = Interlocked.Exchange(ref _dllLoadFlag, @true);
            if (loadFlag == @true)
            {
                return;
            }

            try
            {
                var assemblyLocation = Assembly.GetExecutingAssembly().Location;
                if (String.IsNullOrWhiteSpace(assemblyLocation))
                {
                    return;
                }

                var directory = Path.GetDirectoryName(assemblyLocation);
                if (String.IsNullOrWhiteSpace(directory))
                {
                    return;
                }

                var dlls = Directory.GetFiles(directory, "quickfix.*.dll");
                foreach (var path in dlls)
                {
                    Assembly.LoadFrom(path);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Found quickfix.*.dll dlls but failed to load them, " + ex);
            }
        }

        private static ICollection<IMessageFactory> GetMessageFactories(IEnumerable<Assembly> assemblies)
        {
            var factoryTypes = assemblies
                .SelectMany(assembly => assembly.GetExportedTypes())
                .Where(IsMessageFactory)
                .ToList();
            var factories = new List<IMessageFactory>();
            foreach (var factoryType in factoryTypes)
            {
                var factory = (IMessageFactory)Activator.CreateInstance(factoryType);
                factories.Add(factory);
            }

            return factories;
        }

        private static ICollection<Assembly> GetAppDomainAssemblies()
        {
            LoadLocalDlls();
            var assemblies = AppDomain
                .CurrentDomain
                .GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .ToList();
            return assemblies;
        }

        private static bool IsMessageFactory(Type type)
        {
            return type != typeof(DefaultMessageFactory) &&
                   type.IsClass &&
                   !type.IsAbstract &&
                   typeof(IMessageFactory).IsAssignableFrom(type) &&
                   type.GetConstructor(Type.EmptyTypes) != null;
        }

        #endregion
    }
}
