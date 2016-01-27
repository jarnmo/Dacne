﻿using System;

namespace Futurice.DataAccess
{

    /// <summary>
    /// Identifies a model of a specific type.
    /// </summary>
    public class ModelIdentifier : IEquatable<ModelIdentifier>
    {
        public ModelIdentifier(string id)
        {
            Id = id;
        }

        public string Id { get; private set; }

        public int Completness { get; }

        public virtual bool Equals(ModelIdentifier other)
        {
            return Id == other.Id;
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }
    }
}