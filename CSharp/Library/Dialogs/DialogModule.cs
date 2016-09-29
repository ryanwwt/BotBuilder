﻿// 
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.
// 
// Microsoft Bot Framework: http://botframework.com
// 
// Bot Builder SDK Github:
// https://github.com/Microsoft/BotBuilder
// 
// Copyright (c) Microsoft Corporation
// All rights reserved.
// 
// MIT License:
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Resources;
using System.Text.RegularExpressions;

using Microsoft.Bot.Builder.Internals.Fibers;
using Microsoft.Bot.Builder.Internals.Scorables;
using Microsoft.Bot.Connector;

using Autofac;
using Microsoft.Bot.Builder.History;

namespace Microsoft.Bot.Builder.Dialogs.Internals
{
    /// <summary>
    /// Autofac module for Dialog components.
    /// </summary>
    public sealed class DialogModule : Module
    {
        public const string BlobKey = "DialogState";
        public static readonly object LifetimeScopeTag = typeof(DialogModule);

        public static readonly object Key_DeleteProfile_Regex = new object();

        public static ILifetimeScope BeginLifetimeScope(ILifetimeScope scope, IMessageActivity message)
        {
            var inner = scope.BeginLifetimeScope(LifetimeScopeTag);
            inner.Resolve<IMessageActivity>(TypedParameter.From(message));
            return inner;
        }

        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);

            builder.RegisterModule(new FiberModule<DialogTask>());

            // singleton components

            builder
                .Register(c => new ResourceManager("Microsoft.Bot.Builder.Resource.Resources", typeof(Resource.Resources).Assembly))
                .As<ResourceManager>()
                .SingleInstance();

            // every lifetime scope is driven by a message

            builder
                .Register((c, p) => p.TypedAs<IMessageActivity>())
                .AsSelf()
                .AsImplementedInterfaces()
                .InstancePerMatchingLifetimeScope(LifetimeScopeTag);

            // make the address and cookie available for the lifetime scope

            builder
                .Register(c => Address.FromActivity(c.Resolve<IActivity>()))
                .AsImplementedInterfaces()
                .InstancePerMatchingLifetimeScope(LifetimeScopeTag);

            builder
                .RegisterType<ResumptionCookie>()
                .AsSelf()
                .InstancePerMatchingLifetimeScope(LifetimeScopeTag);

            // components not marked as [Serializable]
            builder
                .RegisterType<MicrosoftAppCredentials>()
                .AsSelf()
                .SingleInstance();

            builder
                // not resolving IEqualityComparer<IAddress> from container because it's a very local policy
                // and yet too broad of an interface.  could explore using tags for registration overrides.
                .Register(c => new LocalMutualExclusion<IAddress>(new ConversationAddressComparer()))
                .As<IScope<IAddress>>()
                .SingleInstance();

            builder
                .Register(c => new ConnectorClientFactory(c.Resolve<IAddress>(), c.Resolve<MicrosoftAppCredentials>()))
                .As<IConnectorClientFactory>()
                .InstancePerLifetimeScope();

            builder
                .Register(c => c.Resolve<IConnectorClientFactory>().MakeConnectorClient())
                .As<IConnectorClient>()
                .InstancePerLifetimeScope();

            builder
                .Register(c => c.Resolve<IConnectorClientFactory>().MakeStateClient())
                .As<IStateClient>()
                .InstancePerLifetimeScope();

            builder
               .Register(c => new DetectChannelCapability(c.Resolve<IAddress>()))
               .As<IDetectChannelCapability>()
               .InstancePerLifetimeScope();

            builder
                .Register(c => c.Resolve<IDetectChannelCapability>().Detect())
                .As<IChannelCapability>()
                .InstancePerLifetimeScope();

            builder.RegisterType<ConnectorStore>()
                .AsSelf()
                .InstancePerLifetimeScope();

            // If bot wants to use InMemoryDataStore instead of 
            // ConnectorStore, the below registration should be used 
            // as the inner IBotDataStore for CachingBotDataStore
            /*builder.RegisterType<InMemoryDataStore>()
                .AsSelf()
                .SingleInstance(); */

            builder.Register(c => new CachingBotDataStore(c.Resolve<ConnectorStore>(),
                                                          CachingBotDataStoreConsistencyPolicy.ETagBasedConsistency))
                .As<IBotDataStore<BotData>>()
                .AsSelf()
                .InstancePerLifetimeScope();

            builder
                .RegisterType<JObjectBotData>()
                .As<IBotData>()
                .InstancePerLifetimeScope();

            builder
                .Register(c => new BotDataBagStream(c.Resolve<IBotData>().PrivateConversationData, BlobKey))
                .As<Stream>()
                .InstancePerLifetimeScope();

            builder
                .RegisterType<DialogTask>()
                .AsSelf()
                .As<IDialogStack>()
                .InstancePerLifetimeScope();

            // Scorable implementing "/deleteprofile"
            builder
                .Register(c => new Regex("^(\\s)*/deleteprofile", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace))
                .Keyed<Regex>(Key_DeleteProfile_Regex)
                .SingleInstance();

            builder
                .Register(c => new DeleteProfileScorable(c.Resolve<IDialogStack>(), c.Resolve<IBotData>(), c.Resolve<IBotToUser>(), c.ResolveKeyed<Regex>(Key_DeleteProfile_Regex)))
                .As<IScorable<IActivity, double>>()
                .InstancePerLifetimeScope();

            builder
                .Register(c =>
                {
                    var stack = c.Resolve<IDialogStack>();
                    var fromStack = stack.Frames.Select(f => f.Target).OfType<IScorable<IActivity, double>>();
                    var fromGlobal = c.Resolve<IScorable<IActivity, double>[]>();
                    // since the stack of scorables changes over time, this should be lazy
                    var lazyScorables = fromStack.Concat(fromGlobal);
                    var scorable = new TraitsScorable<IActivity, double>(c.Resolve<ITraits<double>>(), c.Resolve<IComparer<double>>(), lazyScorables);
                    return scorable;
                })
                .InstancePerLifetimeScope()
                .AsSelf();

            builder
                .RegisterType<NullActivityLogger>()
                .AsImplementedInterfaces()
                .InstancePerLifetimeScope();

            builder
                .Register(c =>
                {
                    var cc = c.Resolve<IComponentContext>();

                    Func<IPostToBot> makeInner = () =>
                    {
                        var task = cc.Resolve<DialogTask>();
                        IDialogStack stack = task;
                        IPostToBot post = task;
                        post = new ReactiveDialogTask(post, stack, cc.Resolve<IStore<IFiberLoop<DialogTask>>>(), cc.Resolve<Func<IDialog<object>>>());
                        post = new ExceptionTranslationDialogTask(post);
                        post = new LocalizedDialogTask(post);
                        post = new ScoringDialogTask<double>(post, stack, cc.Resolve<TraitsScorable<IActivity, double>>());
                        return post;
                    };

                    IPostToBot outer = new PersistentDialogTask(makeInner, cc.Resolve<IBotData>());
                    outer = new SerializingDialogTask(outer, cc.Resolve<IAddress>(), c.Resolve<IScope<IAddress>>());
                    outer = new PostUnhandledExceptionToUserTask(outer, cc.Resolve<IBotToUser>(), cc.Resolve<ResourceManager>(), cc.Resolve<TraceListener>());
                    outer = new LogToBot(cc.Resolve<IActivityLogger>(), outer);
                    return outer;
                })
                .As<IPostToBot>()
                .InstancePerLifetimeScope();

            builder
                .RegisterType<AlwaysSendDirect_BotToUser>()
                .AsSelf()
                .InstancePerLifetimeScope();

            builder
                .Register(c => new LogToUser(new MapToChannelData_BotToUser(
                    c.Resolve<AlwaysSendDirect_BotToUser>(), 
                    new List<IMessageActivityMapper> { new KeyboardCardMapper() }), c.Resolve<IActivityLogger>())
                .As<IBotToUser>()
                .InstancePerLifetimeScope();

            builder
                .RegisterType<DialogContext>()
                .As<IDialogContext>()
                .InstancePerLifetimeScope();
        }
    }

    public sealed class DialogModule_MakeRoot : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);

            builder.RegisterModule(new DialogModule());

            // TODO: let dialog resolve its dependencies from container
            builder
                .Register((c, p) => p.TypedAs<Func<IDialog<object>>>())
                .AsSelf()
                .InstancePerMatchingLifetimeScope(DialogModule.LifetimeScopeTag);
        }

        public static void Register(ILifetimeScope scope, Func<IDialog<object>> MakeRoot)
        {
            // TODO: let dialog resolve its dependencies from container
            scope.Resolve<Func<IDialog<object>>>(TypedParameter.From(MakeRoot));
        }
    }
}
