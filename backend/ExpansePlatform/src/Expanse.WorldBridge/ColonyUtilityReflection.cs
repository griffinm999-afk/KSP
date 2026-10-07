using System;
using System.Collections.Generic;
using System.Reflection;

namespace Expanse.WorldBridge
{
    // Only CLR metadata is retained. Every observation still reads its current
    // instance, including getters that change or throw after an earlier read.
    internal static class ColonyUtilityReflection
    {
        const int MaxTypes = 512;
        const int MaxMembers = 4096;
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        static readonly object Gate = new object();
        static readonly Dictionary<Type, TypeMetadata> Types = new Dictionary<Type, TypeMetadata>();
        static int memberCount;

        sealed class TypeMetadata
        {
            internal readonly string Identity;
            internal readonly Dictionary<string, MemberMetadata> Members = new Dictionary<string, MemberMetadata>(StringComparer.Ordinal);
            internal TypeMetadata(Type type)
            {
                var assembly = type.Assembly.GetName();
                Identity = type.FullName + "|" + assembly.Name + "|" + assembly.Version;
            }
        }

        sealed class MemberMetadata
        {
            internal readonly FieldInfo Field;
            internal readonly PropertyInfo Property;
            internal MemberMetadata(Type type, string name)
            {
                Field = type.GetField(name, Flags);
                // Preserve the old field-first lookup and its exceptions.
                if (Field == null) Property = type.GetProperty(name, Flags);
            }
            internal object Read(object instance)
            {
                return Field != null ? Field.GetValue(instance) : Property == null ? null : Property.GetValue(instance, null);
            }
        }

        static TypeMetadata Metadata(Type type)
        {
            TypeMetadata metadata;
            if (Types.TryGetValue(type, out metadata)) return metadata;
            if (Types.Count >= MaxTypes)
            {
                Types.Clear();
                memberCount = 0;
            }
            metadata = new TypeMetadata(type);
            Types.Add(type, metadata);
            return metadata;
        }

        internal static string Identity(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            // Emit/TypeBuilder metadata can still change; native loaded types
            // are immutable and keyed by Type, never by a coincident type name.
            if (type.Assembly.IsDynamic) return new TypeMetadata(type).Identity;
            lock (Gate) return Metadata(type).Identity;
        }

        internal static object Read(object instance, string name)
        {
            if (instance == null) return null;
            if (name == null) throw new ArgumentNullException(nameof(name));
            var type = instance as Type ?? instance.GetType();
            MemberMetadata member;
            if (type.Assembly.IsDynamic) member = new MemberMetadata(type, name);
            else lock (Gate)
            {
                var metadata = Metadata(type);
                if (!metadata.Members.TryGetValue(name, out member))
                {
                    member = new MemberMetadata(type, name);
                    if (memberCount >= MaxMembers)
                    {
                        Types.Clear();
                        memberCount = 0;
                        metadata = Metadata(type);
                    }
                    metadata.Members.Add(name, member);
                    memberCount++;
                }
            }
            // Do not retain the object, returned value, exception or a bound
            // delegate. Read outside the metadata lock on every invocation.
            return member.Read(instance is Type ? null : instance);
        }
    }
}
